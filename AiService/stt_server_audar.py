"""Arabic speech-to-text server using Audar-ASR-V1-Flash (GGUF, llama.cpp).

Drop-in alternative to stt_server.py (Whisper): same /transcribe and /health contract, so
the API switches between them with the Stt section in appsettings.json only.

Model: Audar-ASR-V1-Flash, 0.19B audio encoder (mmproj, projector qwen3a) + 0.6B Qwen3
decoder. 30 s audio context. Runs in llama.cpp's llama-server, which this script starts as
a child process on an internal port and talks to over its OpenAI-compatible API. torch and
transformers are not used, so nothing here depends on the transformers version shared
with Surya OCR and the TTS server.

    python stt_server_audar.py                        # http://127.0.0.1:8013
    POST /transcribe  multipart "file" (webm/ogg/wav/mp3/m4a...) -> {"text": ...}
    GET  /health

Env:
    STT_MODEL_DIR     folder with the GGUF files (default C:\\Work\\PROJECTS\\audaraiAudar_ASR_V1_Flash)
    STT_PORT          default 8013
    STT_LLAMA_PORT    internal llama-server port, default 8014
    STT_LLAMA_DIR     llama.cpp build folder (default C:\\Work\\PROJECTS\\llama.cpp-b11042)
    STT_GGUF          decoder file name (default Audar-ASR-V1-Flash.gguf, BF16; the Q4_K_M file
                      is ~2x faster, 0.5 GB less RAM, and got more words wrong)
    STT_THREADS       CPU threads for llama-server (default 8)
"""
import atexit
import base64
import io
import json
import os
import re
import subprocess
import threading
import time
import unicodedata
import urllib.request
import wave

import av  # noqa: E402  PyAV decodes browser MediaRecorder output (webm/opus) without ffmpeg.exe
import numpy as np  # noqa: E402
from fastapi import FastAPI, File, HTTPException, UploadFile  # noqa: E402

MODEL_DIR = os.environ.get("STT_MODEL_DIR", r"C:\Work\PROJECTS\audaraiAudar_ASR_V1_Flash")
LLAMA_DIR = os.environ.get("STT_LLAMA_DIR", r"C:\Work\PROJECTS\llama.cpp-b11042")
GGUF = os.environ.get("STT_GGUF", "Audar-ASR-V1-Flash.gguf")
MMPROJ = "mmproj-Audar-ASR-V1-Flash.gguf"
LLAMA_PORT = int(os.environ.get("STT_LLAMA_PORT", "8014"))
THREADS = os.environ.get("STT_THREADS", "8")
LLAMA_URL = f"http://127.0.0.1:{LLAMA_PORT}"

SAMPLE_RATE = 16000
MAX_SECONDS = 120          # a chat question, not a lecture
MAX_BYTES = 15 * 1024 * 1024
# The model's context is 30 s, but on 45 s of repeated test sentences a 26-29 s window came
# back with whole sentences skipped, while 20 s came back complete. Cut at 20 s.
WINDOW = 20 * SAMPLE_RATE
PROMPT = "Transcribe the audio."

_DIACRITICS_RE = re.compile("[\u0610-\u061A\u064B-\u065F\u0670\u06D6-\u06DC\u06DF-\u06E8\u06EA-\u06ED\u0640]")
# (Laughter)-style tags: 3 s of white noise came back as "(Laughter)".
_TAG_RE = re.compile(r"\[[^\]]*\]|<[^>]*>|\([^)]*\)")
# The model answers "language Arabic<asr_text>...". Keep only the transcript.
_PREFIX_RE = re.compile(r"^.*?<asr_text>", re.S)
_PERSIAN_TO_ARABIC = str.maketrans({"\u06CC": "\u064A", "\u06A9": "\u0643"})


def clean(text: str) -> str:
    text = _PREFIX_RE.sub("", text)
    text = unicodedata.normalize("NFC", text)
    text = _DIACRITICS_RE.sub("", _TAG_RE.sub(" ", text)).translate(_PERSIAN_TO_ARABIC)
    return " ".join(text.split())


def decode_audio(data: bytes) -> np.ndarray:
    """Any container/codec PyAV reads -> 16 kHz mono float32."""
    resampler = av.AudioResampler(format="flt", layout="mono", rate=SAMPLE_RATE)
    chunks = []
    with av.open(io.BytesIO(data)) as container:
        stream = next((s for s in container.streams if s.type == "audio"), None)
        if stream is None:
            raise ValueError("no audio stream")
        for frame in container.decode(stream):
            frame.pts = None
            for out in resampler.resample(frame):
                chunks.append(out.to_ndarray().reshape(-1))
        for out in resampler.resample(None):
            chunks.append(out.to_ndarray().reshape(-1))
    if not chunks:
        raise ValueError("empty audio")
    return np.concatenate(chunks).astype(np.float32)


def to_wav(audio: np.ndarray) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SAMPLE_RATE)
        w.writeframes((np.clip(audio, -1, 1) * 32767).astype(np.int16).tobytes())
    return buf.getvalue()


def split_windows(audio: np.ndarray) -> list:
    """Cut audio longer than WINDOW at the quietest 100 ms in the last 5 s of each window,
    so a cut rarely lands inside a word."""
    parts, start, hop = [], 0, SAMPLE_RATE // 10
    while len(audio) - start > WINDOW:
        lo, hi = start + WINDOW - 5 * SAMPLE_RATE, start + WINDOW
        seg = audio[lo:hi]
        energy = [float(np.mean(seg[i:i + hop] ** 2)) for i in range(0, len(seg) - hop + 1, hop)]
        cut = lo + int(np.argmin(energy)) * hop + hop // 2
        parts.append(audio[start:cut])
        start = cut
    parts.append(audio[start:])
    return parts


# ---- llama-server child process -------------------------------------------------------

_llama = None
_lock = threading.Lock()


def start_llama():
    global _llama
    exe = os.path.join(LLAMA_DIR, "llama-server.exe")
    args = [exe, "-m", os.path.join(MODEL_DIR, GGUF), "--mmproj", os.path.join(MODEL_DIR, MMPROJ),
            "--host", "127.0.0.1", "--port", str(LLAMA_PORT), "-t", THREADS,
            "-c", "4096", "--parallel", "1", "--no-webui"]
    print("starting:", " ".join(args), flush=True)
    # Child output goes to our stdout, which the C# launcher writes to stt_server_audar.log.
    _llama = subprocess.Popen(args, stdout=None, stderr=subprocess.STDOUT,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    t = time.time()
    while time.time() - t < 180:
        if _llama.poll() is not None:
            raise RuntimeError(f"llama-server exited with code {_llama.returncode}")
        try:
            with urllib.request.urlopen(LLAMA_URL + "/health", timeout=2) as r:
                if r.status == 200:
                    print(f"llama-server ready in {time.time() - t:.1f}s", flush=True)
                    return
        except Exception:
            pass
        time.sleep(0.5)
    raise RuntimeError("llama-server not ready after 180 s")


def stop_llama():
    if _llama is not None and _llama.poll() is None:
        _llama.kill()


atexit.register(stop_llama)


def transcribe_window(audio: np.ndarray) -> str:
    body = {
        "messages": [{"role": "user", "content": [
            {"type": "input_audio", "input_audio": {
                "data": base64.b64encode(to_wav(audio)).decode(), "format": "wav"}},
            {"type": "text", "text": PROMPT}]}],
        "temperature": 0,
        "max_tokens": 400,
    }
    req = urllib.request.Request(LLAMA_URL + "/v1/chat/completions", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        out = json.load(r)
    return clean(out["choices"][0]["message"]["content"])


def transcribe(audio: np.ndarray) -> str:
    with _lock:
        if _llama is None or _llama.poll() is not None:
            start_llama()
        return " ".join(t for t in (transcribe_window(p) for p in split_windows(audio)) if t)


start_llama()
transcribe(np.zeros(SAMPLE_RATE, dtype=np.float32))  # warm-up

app = FastAPI(title="Arabic STT (Audar)")


@app.get("/health")
def health():
    # Restart a dead llama-server here too; otherwise the C# launcher sees 503, tries to start
    # a second copy of this script, and that one can't bind the port this one holds.
    if _llama is None or _llama.poll() is not None:
        try:
            with _lock:
                if _llama is None or _llama.poll() is not None:
                    start_llama()
        except Exception as e:
            raise HTTPException(503, f"llama-server not running: {e}")
    return {"status": "ok", "model": GGUF}


@app.post("/transcribe")
async def transcribe_endpoint(file: UploadFile = File(...)):
    data = await file.read()
    if not data:
        raise HTTPException(400, "empty file")
    if len(data) > MAX_BYTES:
        raise HTTPException(413, "audio too large")
    try:
        audio = decode_audio(data)
    except Exception as e:
        raise HTTPException(400, f"could not decode audio: {e}")
    duration = len(audio) / SAMPLE_RATE
    if duration > MAX_SECONDS:
        raise HTTPException(413, f"audio is {duration:.0f}s; max {MAX_SECONDS}s")
    t = time.time()
    # Near-silent recording (button pressed, nothing said): skip the model.
    if float(np.sqrt(np.mean(audio ** 2))) < 1e-3:
        return {"text": "", "duration": round(duration, 2), "seconds": 0.0}
    text = transcribe(audio)
    if len(text.replace(" ", "")) < 2:
        text = ""
    return {"text": text, "duration": round(duration, 2), "seconds": round(time.time() - t, 2)}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=int(os.environ.get("STT_PORT", "8013")), log_level="info")
