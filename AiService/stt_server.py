"""Arabic speech-to-text server for the chat voice button.

Model: whisper-large-v3-turbo-arabic-dialectal (809M, fine-tuned on multi-dialect Arabic,
model card WER 0.344 / CER 0.115). The model loads once and stays in memory.

    python stt_server.py                              # http://127.0.0.1:8011
    POST /transcribe  multipart "file" (webm/ogg/wav/mp3/m4a...) -> {"text": ...}
    GET  /health

Env:
    STT_MODEL_DIR   model folder (default C:\\Work\\PROJECTS\\WASFYSPEECH\\whisper-large-v3-turbo-arabic-dialectal)
    STT_PORT        default 8011
    STT_DTYPE       float32 (default) or bfloat16 (half the RAM on CPU)
"""
import io
import os
import re
import threading
import time
import unicodedata
import warnings

warnings.filterwarnings("ignore")

import av  # noqa: E402  PyAV decodes browser MediaRecorder output (webm/opus) without ffmpeg.exe
import numpy as np  # noqa: E402
import torch  # noqa: E402
from fastapi import FastAPI, File, HTTPException, UploadFile  # noqa: E402
from transformers import WhisperForConditionalGeneration, WhisperProcessor  # noqa: E402

MODEL_DIR = os.environ.get(
    "STT_MODEL_DIR", r"C:\Work\PROJECTS\WASFYSPEECH\whisper-large-v3-turbo-arabic-dialectal")
DTYPE = {"float32": torch.float32, "bfloat16": torch.bfloat16}[os.environ.get("STT_DTYPE", "float32")]
SAMPLE_RATE = 16000
MAX_SECONDS = 120          # a chat question, not a lecture
MAX_BYTES = 15 * 1024 * 1024
WHISPER_WINDOW = 30 * SAMPLE_RATE

# The model was saved by transformers 5.x, which writes extra_special_tokens as a list;
# 4.57 (installed here, shared with Surya OCR and the TTS server) expects a dict and
# crashes. Those tokens are Whisper's standard ones and are already in tokenizer.json,
# so dropping the field loads identical ids (<|ar|>=50272, <|transcribe|>=50360).
processor = WhisperProcessor.from_pretrained(MODEL_DIR, extra_special_tokens={})
model = WhisperForConditionalGeneration.from_pretrained(
    MODEL_DIR, dtype=DTYPE, low_cpu_mem_usage=True).eval()
# Also saved by 5.x: eos_token_id is [50257]. 4.57's timestamp processor (used for audio
# over 30 s) slices with it and fails with "slice indices must be integers".
if isinstance(model.generation_config.eos_token_id, list):
    model.generation_config.eos_token_id = model.generation_config.eos_token_id[0]
_lock = threading.Lock()

# Same cleaning the model was trained and scored with (normalize.py in the model folder):
# no tashkil/tatweel, no [laughter]-style tags. Persian yeh/kaf -> Arabic so the text
# matches the indexed documents.
_DIACRITICS_RE = re.compile("[\u0610-\u061A\u064B-\u065F\u0670\u06D6-\u06DC\u06DF-\u06E8\u06EA-\u06ED\u0640]")
_TAG_RE = re.compile(r"\[[^\]]*\]|<[^>]*>")
_PERSIAN_TO_ARABIC = str.maketrans({"\u06CC": "\u064A", "\u06A9": "\u0643"})


def clean(text: str) -> str:
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


def transcribe(audio: np.ndarray) -> str:
    if len(audio) <= WHISPER_WINDOW:
        inputs = processor(audio, sampling_rate=SAMPLE_RATE, return_tensors="pt")
        kwargs = {}
    else:
        # Whisper sees 30 s at a time; long-form generation slides over the audio using
        # timestamps instead of cutting words at fixed 30 s boundaries.
        inputs = processor(audio, sampling_rate=SAMPLE_RATE, return_tensors="pt",
                           truncation=False, padding="longest", return_attention_mask=True)
        kwargs = {"attention_mask": inputs.attention_mask, "return_timestamps": True}
    with _lock, torch.inference_mode():
        ids = model.generate(inputs.input_features.to(DTYPE), language="ar", task="transcribe", **kwargs)
    return clean(processor.batch_decode(ids, skip_special_tokens=True)[0])


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
    # Near-silent recording (button pressed, nothing said): skip the model. Whisper
    # invents sentences on silence. Background noise still gets through (3 s of white
    # noise came back as "إيه ده!"), and <|nospeech|> can't catch it: the fine-tune scores
    # 0.000 for speech and noise alike.
    if float(np.sqrt(np.mean(audio ** 2))) < 1e-3:
        return {"text": "", "duration": round(duration, 2), "seconds": 0.0}
    text = transcribe(audio)
    if len(text.replace(" ", "")) < 2:
        text = ""
    return {"text": text, "duration": round(duration, 2), "seconds": round(time.time() - t, 2)}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=int(os.environ.get("STT_PORT", "8011")), log_level="info")
