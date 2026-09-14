"""Arabic speech-to-text server for the chat voice button (wav2vec2-xls-r-300m CTC).

The model loads once and stays in memory: loading takes ~48 s on this CPU, while a
30 s recording then transcribes in ~6 s.

    python stt_server.py                              # http://127.0.0.1:8011
    POST /transcribe  multipart "file" (webm/ogg/wav/mp3/m4a...) -> {"text": ...}
    GET  /health

Env:
    STT_MODEL_DIR   model folder (default C:\\Work\\PROJECTS\\speechToText\\arabic_speech_to_text)
    STT_PORT        default 8011
"""
import io
import os
import threading
import time
import warnings

warnings.filterwarnings("ignore")

import av  # noqa: E402  PyAV decodes browser MediaRecorder output (webm/opus) without ffmpeg.exe
import numpy as np  # noqa: E402
import torch  # noqa: E402
from fastapi import FastAPI, File, HTTPException, UploadFile  # noqa: E402
from transformers import Wav2Vec2ForCTC, Wav2Vec2Processor  # noqa: E402

MODEL_DIR = os.environ.get("STT_MODEL_DIR", r"C:\Work\PROJECTS\speechToText\arabic_speech_to_text")
SAMPLE_RATE = 16000
MAX_SECONDS = 120          # a chat question, not a lecture
WINDOW_SECONDS = 30        # attention cost grows with length; decode long audio in windows
MAX_BYTES = 15 * 1024 * 1024

processor = Wav2Vec2Processor.from_pretrained(MODEL_DIR)
model = Wav2Vec2ForCTC.from_pretrained(MODEL_DIR).eval()
_lock = threading.Lock()

# The vocab contains Persian yeh (ی); it never matches ي in the indexed documents.
_PERSIAN_TO_ARABIC = str.maketrans({"\u06CC": "\u064A", "\u06A9": "\u0643"})


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


def transcribe(audio: np.ndarray) -> str:
    step = WINDOW_SECONDS * SAMPLE_RATE
    parts = []
    with _lock, torch.inference_mode():
        for start in range(0, len(audio), step):
            window = audio[start:start + step]
            if len(window) < SAMPLE_RATE // 4:      # <0.25 s tail: nothing to say
                continue
            inputs = processor(window, sampling_rate=SAMPLE_RATE, return_tensors="pt")
            logits = model(inputs.input_values).logits
            parts.append(processor.batch_decode(logits.argmax(-1))[0])
    text = " ".join(p.strip() for p in parts if p.strip())
    text = " ".join(text.translate(_PERSIAN_TO_ARABIC).split())
    # CTC on silence or noise emits a stray letter (2 s of silence gave "ة").
    return text if len(text.replace(" ", "")) >= 2 else ""


transcribe(np.zeros(SAMPLE_RATE, dtype=np.float32))  # warm-up

app = FastAPI(title="Arabic STT")


@app.get("/health")
def health():
    return {"status": "ok", "model": os.path.basename(MODEL_DIR)}


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
    # near-silent recording (button pressed, nothing said): skip the model
    if float(np.sqrt(np.mean(audio ** 2))) < 1e-3:
        return {"text": "", "duration": round(duration, 2), "seconds": 0.0}
    text = transcribe(audio)
    return {"text": text, "duration": round(duration, 2), "seconds": round(time.time() - t, 2)}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=int(os.environ.get("STT_PORT", "8011")), log_level="info")
