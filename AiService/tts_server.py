"""Arabic text-to-speech server for the Chat endpoint (Nabra-7M-Distill).

The model loads once and stays in memory. Starting Python per request would add
torch import + model load to every reply.

    python tts_server.py                         # http://127.0.0.1:8010
    POST /tts  {"text": "...", "speed": 1.0}  -> audio/wav (24 kHz, 16-bit mono)
    GET  /health

Env:
    NABRA_DIR   model folder (default C:\\Work\\PROJECTS\\Nabra\\Nabra-7M-Distill)
    TTS_PORT    default 8010
"""
import io
import os
import re
import sys
import threading
import types

NABRA_DIR = os.environ.get("NABRA_DIR", r"C:\Work\PROJECTS\Nabra\Nabra-7M-Distill")
MAX_CHARS = 4000

# kokoro's pipeline imports misaki.en, which needs spaCy. spaCy does not build on
# Python 3.13 and the Arabic path never touches it, so a stub is enough.
_en = types.ModuleType("misaki.en")
_en.MToken = object
_en.G2P = None
sys.modules["misaki.en"] = _en
import misaki  # noqa: E402
misaki.en = _en

# camel-tools' MLE diacritizer imports `editdistance`, which has no Windows wheel for
# 3.13 and needs a C++ compiler. It only calls editdistance.eval; rapidfuzz computes
# the same Levenshtein distance. camel-tools was installed with --no-deps for this.
try:
    import editdistance  # noqa: F401
except ImportError:
    from rapidfuzz.distance import Levenshtein as _lev
    _ed = types.ModuleType("editdistance")
    _ed.eval = _lev.distance
    sys.modules["editdistance"] = _ed

# misaki only looks for espeak-ng under Program Files; use the DLL bundled in espeakng_loader.
import espeakng_loader  # noqa: E402
from phonemizer.backend.espeak.wrapper import EspeakWrapper  # noqa: E402
EspeakWrapper.set_library(espeakng_loader.get_library_path())
EspeakWrapper.set_data_path(espeakng_loader.get_data_path())

sys.path.insert(0, NABRA_DIR)
import numpy as np  # noqa: E402
import soundfile as sf  # noqa: E402
import torch  # noqa: E402
from fastapi import FastAPI, HTTPException  # noqa: E402
from fastapi.responses import Response  # noqa: E402
from pydantic import BaseModel  # noqa: E402
from load_model import load  # noqa: E402

SAMPLE_RATE = 24000

from arabic_g2p import ArabicG2P, clean_phonemes, normalize_text  # noqa: E402

model, pipeline, voice = load()

# load_model.py wraps g2p as `_orig(t)[0]`, but misaki 0.7.x (the last that installs
# on 3.13) returns a plain string, so [0] kept only the first phoneme. Replace it.
# ArabicG2P adds camel-tools tashkeel when installed, otherwise passes text through.
_arabic = ArabicG2P(diacritize=True)
_espeak = pipeline.g2p.__closure__[0].cell_contents if pipeline.g2p.__closure__ else None


def _g2p(text):
    text, _ = normalize_text(text)
    out = _espeak(_arabic.diacritize(text))
    ph = out[0] if isinstance(out, tuple) else out
    return clean_phonemes(ph), None


pipeline.g2p = _g2p
_lock = threading.Lock()  # KPipeline is not thread-safe

# Markdown from the LLM would otherwise be read out as symbols.
_MARKDOWN = re.compile(r"[*#_`>|~]+|\[(.*?)\]\([^)]*\)")


def clean_for_speech(text: str) -> str:
    text = _MARKDOWN.sub(lambda m: m.group(1) or " ", text)
    text = text.replace("؟", "?").replace("،", ",").replace("؛", ";")
    return re.sub(r"\s+", " ", text).strip()


def synthesize(text: str, speed: float) -> bytes:
    parts = []
    with _lock, torch.inference_mode():
        # One line per segment keeps each chunk inside the 510-phoneme limit.
        for line in filter(None, (l.strip() for l in re.split(r"[\n.!?]+", text))):
            for result in pipeline(line, voice=voice, speed=speed, split_pattern=None):
                if result.audio is not None:
                    parts.append(result.audio.cpu().numpy())
                    parts.append(np.zeros(int(0.15 * SAMPLE_RATE), dtype=np.float32))
    if not parts:
        raise ValueError("no speakable text")
    buf = io.BytesIO()
    sf.write(buf, np.concatenate(parts), SAMPLE_RATE, format="WAV", subtype="PCM_16")
    return buf.getvalue()


synthesize("مرحبا بكم.", 1.0)  # warm-up: the first call is ~4x slower than later ones


class TtsRequest(BaseModel):
    text: str
    speed: float = 1.0


app = FastAPI(title="Nabra TTS")


@app.get("/health")
def health():
    return {"status": "ok", "model": "Nabra-7M-Distill", "sample_rate": SAMPLE_RATE}


@app.post("/tts")
def tts(req: TtsRequest):
    text = clean_for_speech(req.text)[:MAX_CHARS]
    if not text:
        raise HTTPException(400, "text is empty")
    try:
        wav = synthesize(text, max(0.5, min(req.speed, 2.0)))
    except ValueError as e:
        raise HTTPException(400, str(e))
    return Response(content=wav, media_type="audio/wav")


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=int(os.environ.get("TTS_PORT", "8010")))
