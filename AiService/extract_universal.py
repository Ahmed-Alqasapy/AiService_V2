# -*- coding: utf-8 -*-
"""
Universal Arabic-first document extractor for RAG.

Replaces extract_claude_up.py. Key differences:

  * PDF text comes from whichever backend is measurably correct for THIS
    document. PyMuPDF mis-maps lam-alef ligatures in many Arabic fonts
    (الم -> امل, في -> يف); pypdf does not. We score both and keep the winner.
  * Pages with no usable text layer are OCR'd with Surya (correct Arabic),
    per page, on demand -- never "force OCR everything".
  * Table geometry comes from PyMuPDF find_tables(), but cell text is
    re-assembled from word boxes sorted right-to-left, because find_tables()
    returns Arabic cell text character-reversed.
  * Tables reach the chunks. Each row is serialised as a self-contained
    sentence carrying its headers, so "كم رسم نقل ملكية البراءة؟" matches.
  * docx / xlsx / pptx / html / images / text are first-class inputs and go
    through the same cleaning, sectioning and chunking as PDFs.

CLI contract is unchanged from extract_claude_up.py:
    python extract_universal.py <input_file_or_dir> <output_dir>
and it still writes <basename>_clean.txt, which is what Extractor.cs reads.
"""
from __future__ import annotations

import hashlib
import io
import json
import logging
import os
import sys
import time
import unicodedata
from dataclasses import dataclass, field, asdict
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

try:
    import regex as re
except ImportError:  # pragma: no cover
    import re  # type: ignore

# --------------------------------------------------------------------------
# logging -- Windows consoles are often cp1256; never let a log line crash a run
# --------------------------------------------------------------------------
LOG = logging.getLogger("extract_universal")

# Surya draws tqdm progress bars on stdout; Extractor.cs captures that
# stream, so keep it clean unless someone is watching interactively.
os.environ.setdefault("TQDM_DISABLE", "1")
os.environ.setdefault("DISABLE_TQDM", "1")


def _setup_logging(verbose: bool = False, log_file: Optional[Path] = None) -> None:
    LOG.setLevel(logging.DEBUG if verbose else logging.INFO)
    LOG.handlers.clear()
    fmt = logging.Formatter("%(asctime)s %(levelname)-7s %(message)s", "%H:%M:%S")

    stream = sys.stdout
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except Exception:
        pass
    sh = logging.StreamHandler(stream)
    sh.setFormatter(fmt)
    LOG.addHandler(sh)

    if log_file is not None:
        try:
            fh = logging.FileHandler(log_file, encoding="utf-8")
            fh.setFormatter(fmt)
            LOG.addHandler(fh)
        except Exception:
            pass


# --------------------------------------------------------------------------
# optional dependencies -- probed lazily, never fatal
# --------------------------------------------------------------------------
def _try(name: str):
    try:
        import importlib

        return importlib.import_module(name)
    except Exception:
        return None


fitz = _try("fitz")            # PyMuPDF: geometry, tables, rendering
pypdf = _try("pypdf")          # correct Arabic ligatures
docx_mod = _try("docx")        # python-docx
openpyxl = _try("openpyxl")
pptx_mod = _try("pptx")        # python-pptx
bs4 = _try("bs4")
chardet = _try("chardet")
PIL_Image = _try("PIL.Image")   # Pillow; imported locally where images are handled
tiktoken = _try("tiktoken")

if fitz is None:
    raise SystemExit("PyMuPDF (fitz) is required: pip install pymupdf")


# ==========================================================================
# ARABIC TEXT
# ==========================================================================
ARABIC_RANGE = r"\u0600-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFF"

# Bidi/format controls PDF producers inject. They are invisible, survive into
# embeddings, and split otherwise-identical strings into different vectors.
BIDI_CONTROLS = re.compile(r"[\u200B-\u200F\u202A-\u202E\u2066-\u2069\uFEFF\u00AD]")
TATWEEL = re.compile(r"\u0640+")                      # kashida justification filler
DIACRITICS = re.compile(r"[\u064B-\u0652\u0670\u06D6-\u06ED]")
ARABIC_INDIC = {ord(c): ord(d) for c, d in zip("٠١٢٣٤٥٦٧٨٩", "0123456789")}
ARABIC_INDIC.update({ord(c): ord(d) for c, d in zip("۰۱۲۳۴۵۶۷۸۹", "0123456789")})

# Signatures of the PyMuPDF/pdfium lam-ligature transposition. Each pair is
# (corrupt form, correct form): the ligature's two characters come out swapped.
_LIGATURE_SIGNATURES: List[Tuple[str, str]] = [
    ("امل", "الم"), ("اخل", "الخ"), ("اجل", "الج"), ("احل", "الح"),
    ("اهل", "اله"), ("اعل", "العل"),
    ("واال", "والا"), ("واإل", "والإ"), ("األ", "الأ"), ("اإل", "الإ"),
    ("االس", "الاس"), ("االق", "الاق"), ("االت", "الات"), ("االن", "الان"),
]
CORRUPT_RE = re.compile("|".join(re.escape(c) for c, _ in _LIGATURE_SIGNATURES) + r"|\bيف\b|\bمل\b")
HEALTHY_RE = re.compile(r"الم|الخ|الج|الح|والا|والإ|الأ|الإ|\bفي\b|\bلم\b")

# Presentation forms: their presence means the producer emitted shaped glyphs
# instead of logical characters, which usually also means visual ordering.
PRESENTATION_RE = re.compile(r"[\uFB50-\uFDFF\uFE70-\uFEFF]")


def ligature_corruption_score(text: str) -> Tuple[int, int, float]:
    """Return (corrupt_hits, healthy_hits, ratio).

    ratio near 1.0 means the text is badly damaged, near 0.0 means it is clean.
    Measured on the raw extractor output -- before any normalisation, because
    normalisation would hide the evidence we are trying to weigh.
    """
    if not text:
        return 0, 0, 0.0
    bad = len(CORRUPT_RE.findall(text))
    good = len(HEALTHY_RE.findall(text))
    total = bad + good
    return bad, good, (bad / total) if total else 0.0


def lossy_cmap_score(text: str) -> Dict[str, float]:
    """Detect a text layer that is silently DROPPING characters.

    Some Arabic fonts ship a ToUnicode CMap with no entry for the lam-alef and
    yaa-nun ligature glyphs. Every extractor then omits them, so the PDF says
    'والصاة والسام' where the page visibly reads 'والصلاة والسلام', and
    'العالمن' for 'العالمين'. Nothing in the text layer can recover this --
    the characters are simply not in the file.

    'لا' and word-final 'ين' are among the most frequent sequences in Arabic
    prose, so a document where they are near-absent is losing them.
    """
    arabic = len(re.findall(rf"[{ARABIC_RANGE}]", text))
    if arabic < 500:
        return {"arabic_chars": arabic, "lam_alef_per_1k": -1.0,
                "yaa_nun_per_1k": -1.0, "suspect": 0.0}
    la = len(re.findall(r"لا", text)) * 1000.0 / arabic
    yn = len(re.findall(r"ين\b", text)) * 1000.0 / arabic
    suspect = 1.0 if (yn < 2.0 or la < 6.0) else 0.0
    return {"arabic_chars": arabic, "lam_alef_per_1k": round(la, 2),
            "yaa_nun_per_1k": round(yn, 2), "suspect": suspect}


def looks_reversed(text: str, sample: int = 4000) -> bool:
    """Detect visual-order (right-to-left reversed) Arabic.

    In logical order the definite article 'ال' is common word-initially; in
    reversed text you instead see its mirror 'لا' word-initially at a rate that
    does not occur in real Arabic prose.
    """
    s = text[:sample]
    if len(re.findall(rf"[{ARABIC_RANGE}]", s)) < 50:
        return False
    fwd = len(re.findall(r"\bال[\u0621-\u064A]", s))
    rev = len(re.findall(r"[\u0621-\u064A]لا\b", s))
    return rev > fwd * 2 and rev >= 5


def repair_ligatures(text: str) -> Tuple[str, int]:
    """Undo the lam-ligature transposition.

    Only used where a correct backend is unavailable (notably table cells,
    which we can only read through PyMuPDF). Applied word-initially where the
    pattern is the Arabic definite article, so ordinary words that merely
    contain the same letters are left alone.
    """
    if not text:
        return text, 0
    n = 0
    out = text
    for corrupt, correct in _LIGATURE_SIGNATURES:
        if corrupt in out:
            hits = out.count(corrupt)
            out = out.replace(corrupt, correct)
            n += hits
    # standalone 'يف' is always a corrupted 'في'
    out, k = re.subn(r"\bيف\b", "في", out)
    n += k
    # word-final 'يني' is the same transposition applied to the 'ين'
    # ending ('التعيني' -> 'التعيين'). Restricted to word-final, and only
    # reached when the document already scored as badly transposed,
    # because a few genuine words ('ديني') share the sequence.
    out, k = re.subn(r"يني\b", "يين", out)
    n += k
    return out, n


def reverse_visual_arabic(text: str) -> str:
    """Restore logical order for a visually-ordered line.

    Arabic runs are reversed; Latin/digit runs inside them are already in
    logical order and must be preserved as-is.
    """
    out_lines = []
    token = re.compile(rf"[{ARABIC_RANGE}]+|[^{ARABIC_RANGE}]+")
    for line in text.split("\n"):
        parts = token.findall(line)
        parts.reverse()
        rebuilt = []
        for p in parts:
            if re.match(rf"^[{ARABIC_RANGE}]+$", p):
                rebuilt.append(p[::-1])
            else:
                rebuilt.append(p)
        out_lines.append("".join(rebuilt))
    return "\n".join(out_lines)


class ArabicNormalizer:
    """Cleaning that is safe for both display and embedding.

    Deliberately does NOT try to split 'stuck' words with heuristic regexes.
    The old pipeline's aggressive_word_separation() turned المعلومات into
    'المعل ومات' and التحويلات into 'التح ويلات' -- it manufactured the very
    damage its STUCK_WORDS table then tried to repair. With a correct backend
    the stuck-word problem largely disappears at the source.
    """

    def __init__(self, strip_diacritics: bool = True, normalize_digits: bool = True):
        self.strip_diacritics = strip_diacritics
        self.normalize_digits = normalize_digits

    def clean(self, text: str) -> str:
        if not text:
            return ""
        # Presentation forms -> logical characters (NFKC also folds ligatures)
        if PRESENTATION_RE.search(text):
            text = unicodedata.normalize("NFKC", text)
        text = BIDI_CONTROLS.sub("", text)
        text = TATWEEL.sub("", text)
        if self.strip_diacritics:
            text = DIACRITICS.sub("", text)
        if self.normalize_digits:
            text = text.translate(ARABIC_INDIC)
        text = text.replace("\u00A0", " ").replace("\t", " ")
        # Surya marks styling inline (<b>Ministry of Commerce</b>, <u>\u0625\u0642\u0631\u0627\u0631</u>);
        # the markup is noise in an embedding, the words inside are not.
        text = re.sub(r"</?(?:b|i|u|em|strong|sub|sup|br|span)\s*/?>", "", text,
                      flags=re.IGNORECASE)
        # image refs and bullet glyphs left over from markdown backends
        text = re.sub(r"!\[[^\]]*\]\([^)]*\)", "", text)
        text = re.sub(r"^[\u25A0-\u25FF\u2190-\u21FF\u2600-\u27BF]\s*", "- ", text, flags=re.M)
        text = re.sub(r"[ ]{2,}", " ", text)
        text = re.sub(r"\n{3,}", "\n\n", text)
        text = "\n".join(ln.rstrip() for ln in text.split("\n"))
        return text.strip()


def normalize_for_match(s: str) -> str:
    """Aggressive fold used only for comparing lines (header/footer detection)."""
    s = BIDI_CONTROLS.sub("", s or "")
    s = TATWEEL.sub("", s)
    s = DIACRITICS.sub("", s)
    s = re.sub(r"[\u0623\u0625\u0622]", "\u0627", s)   # أ إ آ -> ا
    s = re.sub(r"\u0649", "\u064A", s)                  # ى -> ي
    s = re.sub(r"\u0629", "\u0647", s)                  # ة -> ه
    s = re.sub(r"\d+", "#", s)
    s = re.sub(r"\s+", " ", s)
    return s.strip()


# ==========================================================================
# DATA MODEL
# ==========================================================================
@dataclass
class Page:
    """One unit of source content. PDFs -> pages, pptx -> slides, xlsx -> sheets."""
    number: int
    text: str = ""
    source: str = "text"          # text | ocr | ocr-embedded | native
    ocr_images: int = 0

    def to_dict(self) -> Dict:
        return asdict(self)


@dataclass
class Table:
    id: str
    headers: List[str]
    rows: List[List[str]]
    caption: str = ""
    section_id: str = ""
    page_number: int = 0
    source: str = "pdf-geometry"
    rtl: bool = True

    def to_dict(self) -> Dict:
        return asdict(self)

    @property
    def n_cols(self) -> int:
        return max([len(self.headers)] + [len(r) for r in self.rows]) if (self.headers or self.rows) else 0

    def to_markdown(self) -> str:
        if not self.headers and not self.rows:
            return ""
        width = self.n_cols

        def pad(cells: Sequence[str]) -> List[str]:
            c = [str(x).replace("|", r"\|").replace("\n", " ").strip() for x in cells]
            return c + [""] * (width - len(c))

        lines: List[str] = []
        if self.caption:
            lines.append(f"**{self.caption}**")
            lines.append("")
        head = pad(self.headers) if self.headers else [""] * width
        lines.append("| " + " | ".join(head) + " |")
        lines.append("| " + " | ".join(["---"] * width) + " |")
        for r in self.rows:
            lines.append("| " + " | ".join(pad(r)) + " |")
        return "\n".join(lines)

    def to_csv(self) -> str:
        import csv

        buf = io.StringIO()
        w = csv.writer(buf, lineterminator="\n")
        if self.headers:
            w.writerow(self.headers)
        w.writerows(self.rows)
        return buf.getvalue()

    def row_sentences(self) -> List[str]:
        """Serialise each row as a standalone statement carrying its headers.

        This is what makes a table answerable. A whole-table blob embeds badly:
        the number the user wants ends up far from the label that identifies it.
        One row per chunk keeps 'نقل ملكية البراءة' and '70' in the same vector.
        """
        out: List[str] = []
        heads = [h.strip() for h in self.headers] if self.headers else []
        for row in self.rows:
            cells = [str(c).strip() for c in row]
            if not any(cells):
                continue
            # A row whose first cell is the only labelled thing reads better as
            # "<label> — <header>: <value>" pairs.
            label = cells[0] if cells else ""
            parts = []
            for i, cell in enumerate(cells):
                if not cell:
                    continue
                head = heads[i] if i < len(heads) and heads[i] else ""
                if i == 0:
                    continue
                parts.append(f"{head}: {cell}" if head else cell)
            if not parts:
                parts = [label]
                label = ""
            prefix = f"{self.caption} — " if self.caption else ""
            body = f"{label} — " if label else ""
            out.append(f"{prefix}{body}" + "، ".join(parts))
        return out


@dataclass
class Section:
    id: str
    title: str
    content: str
    level: int = 1
    page_start: int = 0
    page_end: int = 0
    parent_id: Optional[str] = None
    heading_path: List[str] = field(default_factory=list)
    word_count: int = 0
    char_count: int = 0
    has_table: bool = False
    has_list: bool = False

    def __post_init__(self):
        self.word_count = len(self.content.split())
        self.char_count = len(self.content)

    def to_dict(self) -> Dict:
        return asdict(self)


@dataclass
class Chunk:
    chunk_id: str
    content: str
    metadata: Dict = field(default_factory=dict)

    def to_dict(self) -> Dict:
        return asdict(self)


@dataclass
class Document:
    title: str
    language: str
    source_file: str
    pages: List[Page]
    sections: List[Section]
    tables: List[Table]
    chunks: List[Chunk]
    full_text: str
    metadata: Dict

    def to_dict(self) -> Dict:
        return {
            "title": self.title,
            "language": self.language,
            "source_file": self.source_file,
            "total_pages": len(self.pages),
            "sections": [s.to_dict() for s in self.sections],
            "tables": [t.to_dict() for t in self.tables],
            "chunks": [c.to_dict() for c in self.chunks],
            "metadata": self.metadata,
        }

    def stats(self) -> Dict:
        words = sum(s.word_count for s in self.sections)
        ocr_pages = sum(1 for p in self.pages if p.source.startswith("ocr"))
        return {
            "total_pages": len(self.pages),
            "ocr_pages": ocr_pages,
            "total_sections": len(self.sections),
            "total_tables": len(self.tables),
            "total_chunks": len(self.chunks),
            "table_chunks": sum(1 for c in self.chunks if c.metadata.get("type", "").startswith("table")),
            "total_words": words,
            "total_chars": len(self.full_text),
            "avg_chunk_words": round(
                sum(len(c.content.split()) for c in self.chunks) / max(len(self.chunks), 1), 1
            ),
        }


def estimate_tokens(text: str) -> int:
    """Word count is a poor proxy for Arabic; use a real tokenizer when we can."""
    if tiktoken is not None:
        try:
            enc = _get_encoder()
            if enc is not None:
                return len(enc.encode(text))
        except Exception:
            pass
    return int(len(text.split()) * 1.4)


_ENCODER = None
_ENCODER_TRIED = False


def _get_encoder():
    global _ENCODER, _ENCODER_TRIED
    if not _ENCODER_TRIED:
        _ENCODER_TRIED = True
        try:
            _ENCODER = tiktoken.get_encoding("cl100k_base")  # type: ignore[union-attr]
        except Exception:
            _ENCODER = None
    return _ENCODER


# ==========================================================================
# OCR (Surya) -- lazy, cached, and only ever called for content we cannot read
# ==========================================================================
class SuryaOCR:
    """Arabic-capable OCR.

    Tesseract is not a usable fallback on this machine: it is v3.02 (2011) and
    ships only 'eng' and 'osd' -- no Arabic data at all. Surya is loaded lazily
    because importing it costs ~14s and pulls torch into memory; documents with
    a complete text layer never pay that cost.
    """

    _instance: Optional["SuryaOCR"] = None
    _unavailable = False

    def __init__(self, cache_dir: Optional[Path] = None):
        self.rec = None
        self.det = None
        self.cache_dir = cache_dir
        if cache_dir is not None:
            try:
                cache_dir.mkdir(parents=True, exist_ok=True)
            except Exception:
                self.cache_dir = None

    @classmethod
    def get(cls, cache_dir: Optional[Path] = None) -> Optional["SuryaOCR"]:
        if cls._unavailable:
            return None
        if cls._instance is None:
            cls._instance = SuryaOCR(cache_dir)
        return cls._instance

    def _ensure_models(self) -> bool:
        if self.rec is not None:
            return True
        if SuryaOCR._unavailable:
            return False
        try:
            t0 = time.time()
            from surya.foundation import FoundationPredictor
            from surya.recognition import RecognitionPredictor
            from surya.detection import DetectionPredictor
            from surya.settings import settings

            self.rec = RecognitionPredictor(
                FoundationPredictor(checkpoint=settings.RECOGNITION_MODEL_CHECKPOINT)
            )
            self.det = DetectionPredictor()
            LOG.info("OCR: Surya models loaded in %.1fs", time.time() - t0)
            return True
        except Exception as e:
            LOG.warning("OCR unavailable (Surya failed to load): %s", e)
            SuryaOCR._unavailable = True
            return False

    def _cache_path(self, digest: str) -> Optional[Path]:
        return (self.cache_dir / (digest + ".txt")) if self.cache_dir else None

    def image_to_text(self, img, digest: Optional[str] = None) -> str:
        """OCR a PIL image. Results are cached by content hash."""
        cp = self._cache_path(digest) if digest else None
        if cp is not None and cp.exists():
            try:
                return cp.read_text(encoding="utf-8")
            except Exception:
                pass
        if not self._ensure_models():
            return ""
        try:
            res = self.rec([img], det_predictor=self.det, math_mode=False)
            lines = [ln.text for ln in res[0].text_lines if (ln.text or "").strip()]
            text = "\n".join(lines)
        except Exception as e:
            LOG.warning("OCR failed on one image: %s", e)
            return ""
        if cp is not None:
            try:
                cp.write_text(text, encoding="utf-8")
            except Exception:
                pass
        return text


def _pil_from_pixmap(pix):
    try:
        from PIL import Image

        return Image.open(io.BytesIO(pix.tobytes("png"))).convert("RGB")
    except Exception:
        return None


# ==========================================================================
# PDF
# ==========================================================================
def open_pdf(path: str):
    """Open a PDF, transparently handling owner-password-only encryption."""
    doc = fitz.open(path)
    if doc.needs_pass:
        if not doc.authenticate(""):
            doc.close()
            raise RuntimeError(
                "PDF is password-protected and needs a user password to read"
            )
        LOG.info("  PDF is owner-password encrypted; opened with empty user password")
    return doc


@dataclass
class BackendResult:
    name: str
    pages: List[str]
    corrupt: int = 0
    healthy: int = 0
    ratio: float = 0.0
    chars: int = 0
    seconds: float = 0.0


class PdfTextBackends:
    """Extract the same PDF twice and keep whichever backend is measurably
    better at Arabic for THIS document.

    Measured on a real 48-page Kuwaiti government PDF:
        PyMuPDF -> 1136 corrupt ligature sequences vs 100 healthy
        pypdf   ->   22 corrupt                   vs 1131 healthy
    The winner is document-specific -- some producers are fine under PyMuPDF,
    and PyMuPDF is ~10x faster -- so we score rather than hardcode.
    """

    @staticmethod
    def pymupdf_pages(doc) -> List[str]:
        out = []
        for pg in doc:
            try:
                out.append(pg.get_text("text") or "")
            except Exception as e:
                LOG.warning("PyMuPDF page %d failed: %s", pg.number + 1, e)
                out.append("")
        return out

    @staticmethod
    def pypdf_pages(path: str, n_pages: int) -> List[str]:
        if pypdf is None:
            return []
        try:
            reader = pypdf.PdfReader(path)
            if getattr(reader, "is_encrypted", False):
                try:
                    reader.decrypt("")
                except Exception:
                    return []
        except Exception as e:
            LOG.warning("pypdf could not open the file: %s", e)
            return []
        out = []
        for i in range(min(n_pages, len(reader.pages))):
            try:
                out.append(reader.pages[i].extract_text() or "")
            except Exception:
                out.append("")
        out += [""] * (n_pages - len(out))
        return out

    @classmethod
    def score(cls, name: str, pages: List[str], seconds: float) -> BackendResult:
        joined = "\n".join(pages)
        bad, good, ratio = ligature_corruption_score(joined)
        return BackendResult(name, pages, bad, good, ratio, len(joined), seconds)

    @classmethod
    def choose(cls, path: str, doc) -> Tuple[BackendResult, List[BackendResult]]:
        results: List[BackendResult] = []

        t0 = time.time()
        results.append(cls.score("pymupdf", cls.pymupdf_pages(doc), time.time() - t0))

        t0 = time.time()
        pp = cls.pypdf_pages(path, len(doc))
        if pp:
            results.append(cls.score("pypdf", pp, time.time() - t0))

        for r in results:
            LOG.info(
                "  backend %-8s %6.2fs  chars=%-7d corrupt=%-5d healthy=%-5d damage=%.1f%%",
                r.name, r.seconds, r.chars, r.corrupt, r.healthy, r.ratio * 100,
            )

        usable = [r for r in results if r.chars > 0]
        if not usable:
            return results[0], results

        # Prefer the least-damaged backend. Rounding keeps near-ties from being
        # decided by noise, and the char count only breaks an actual tie -- we
        # never trade correctness for a few extra characters.
        best = min(usable, key=lambda r: (round(r.ratio, 2), -r.chars))
        LOG.info("  -> using %s", best.name)
        return best, results


class PdfExtractor:
    def __init__(
        self,
        ocr: Optional[SuryaOCR] = None,
        ocr_enabled: bool = True,
        ocr_dpi: int = 200,
        min_page_chars: int = 60,
        ocr_embedded: bool = True,
        ocr_on_lossy: bool = False,
        verify_lossy: bool = False,
    ):
        self.ocr = ocr
        self.ocr_enabled = ocr_enabled
        self.ocr_dpi = ocr_dpi
        self.min_page_chars = min_page_chars
        self.ocr_embedded = ocr_embedded
        self.ocr_on_lossy = ocr_on_lossy
        self.verify_lossy = verify_lossy

    def _probe_lossy(self, doc, best: BackendResult) -> Dict[str, Any]:
        """Confirm a suspected lossy CMap by OCR-ing the densest page.

        The cheap frequency test can be fooled by short or unusual documents,
        so we only spend one OCR page on it, and only when that test fires.
        """
        text = "\n".join(best.pages)
        score = lossy_cmap_score(text)
        result: Dict[str, Any] = {"text_layer_metrics": score, "text_layer_lossy": False}
        if not score.get("suspect"):
            return result
        result["text_layer_lossy"] = True
        result["text_layer_lossy_confirmed"] = False
        # Confirming by OCR costs minutes per page on CPU, and if we are going
        # to OCR the document anyway the probe tells us nothing new. So the
        # frequency test stands on its own unless confirmation is asked for.
        if not self.verify_lossy or self.ocr_on_lossy or not self.ocr_enabled or self.ocr is None:
            return result

        idx = max(range(len(best.pages)), key=lambda i: len(best.pages[i]))
        LOG.info("  text layer looks lossy; probing page %d with OCR...", idx + 1)
        ocr_text = self._ocr_page(doc, idx, reason="lossy-CMap probe")
        ar_txt = len(re.findall(rf"[{ARABIC_RANGE}]", best.pages[idx]))
        ar_ocr = len(re.findall(rf"[{ARABIC_RANGE}]", ocr_text))
        gain = (ar_ocr - ar_txt) / max(ar_txt, 1)
        result["ocr_probe_page"] = idx + 1
        result["ocr_probe_gain"] = round(gain, 3)
        result["text_layer_lossy"] = bool(gain > 0.05)
        result["text_layer_lossy_confirmed"] = True
        return result

    def extract_pages(self, path: str) -> Tuple[List[Page], Dict]:
        doc = open_pdf(path)
        try:
            best, all_results = PdfTextBackends.choose(path, doc)
            lossy = self._probe_lossy(doc, best)
            force_ocr_all = False
            if lossy.get("text_layer_lossy"):
                m = lossy["text_layer_metrics"]
                LOG.warning(
                    "  TEXT LAYER IS LOSSY: this PDF's fonts drop lam-alef/yaa ligatures "
                    "(لا=%.1f/1k, ين=%.1f/1k). Words like 'الصلاة' arrive as 'الصاة'. "
                    "No text extractor can recover them.",
                    m.get("lam_alef_per_1k", -1), m.get("yaa_nun_per_1k", -1),
                )
                if self.ocr_on_lossy and self.ocr_enabled:
                    LOG.warning("  --ocr-on-lossy-text is set: OCR-ing every page instead.")
                    force_ocr_all = True
                else:
                    LOG.warning("  re-run with --ocr-on-lossy-text to read these pages by OCR.")

            pages: List[Page] = []
            ocr_count = 0
            reversed_fixed = 0

            for i in range(len(doc)):
                raw = best.pages[i] if i < len(best.pages) else ""
                source = "text"

                if looks_reversed(raw):
                    raw = reverse_visual_arabic(raw)
                    reversed_fixed += 1

                needs_ocr = force_ocr_all or len(raw.strip()) < self.min_page_chars
                if needs_ocr and self.ocr_enabled:
                    ocr_text = self._ocr_page(doc, i)
                    if len(ocr_text.strip()) > len(raw.strip()) * (0.6 if force_ocr_all else 1.0):
                        raw, source = ocr_text, "ocr"
                        ocr_count += 1

                page = Page(number=i + 1, text=raw, source=source)

                # A page can have a fine text layer and still hide a table or a
                # form inside an image -- content that is invisible today.
                if self.ocr_enabled and self.ocr_embedded and source == "text":
                    extra, n = self._ocr_embedded_images(doc, i)
                    if extra:
                        page.text = (page.text + "\n\n" + extra).strip()
                        page.ocr_images = n
                        page.source = "text+ocr-embedded"

                pages.append(page)

            meta = {
                "backend": best.name,
                "backends_tried": [
                    {"name": r.name, "chars": r.chars, "corrupt": r.corrupt,
                     "healthy": r.healthy, "damage_ratio": round(r.ratio, 4),
                     "seconds": round(r.seconds, 2)}
                    for r in all_results
                ],
                "ligature_damage_ratio": round(best.ratio, 4),
                # table cells are always read through PyMuPDF, so the repair
                # decision must follow PyMuPDF's damage, not the winner's
                "pymupdf_damage_ratio": round(
                    next((r.ratio for r in all_results if r.name == "pymupdf"), 0.0), 4
                ),
                "ocr_pages": ocr_count,
                "reversed_pages_fixed": reversed_fixed,
                "ocr_embedded_images": sum(p.ocr_images for p in pages),
                "pdf_metadata": dict(doc.metadata or {}),
                "toc": doc.get_toc() or [],
            }
            meta.update(lossy)
            return pages, meta
        finally:
            doc.close()

    def _ocr_page(self, doc, index: int, reason: str = "no usable text layer") -> str:
        if self.ocr is None:
            return ""
        try:
            pix = doc[index].get_pixmap(dpi=self.ocr_dpi)
            digest = hashlib.sha1(pix.tobytes("png")).hexdigest()
            img = _pil_from_pixmap(pix)
            if img is None:
                return ""
            LOG.info("  OCR page %d (%s)...", index + 1, reason)
            return self.ocr.image_to_text(img, digest)
        except Exception as e:
            LOG.warning("  OCR failed for page %d: %s", index + 1, e)
            return ""

    def _ocr_embedded_images(self, doc, index: int) -> Tuple[str, int]:
        """OCR sizeable images embedded in an otherwise-readable page.

        Logos, rules and icons are filtered out by absolute size and aspect
        ratio, so we do not spend 27 CPU-seconds reading a ministry crest.
        """
        if self.ocr is None:
            return "", 0
        page = doc[index]
        parts: List[str] = []
        n = 0
        try:
            images = page.get_images(full=True)
        except Exception:
            return "", 0
        for info in images:
            xref = info[0]
            try:
                pix = fitz.Pixmap(doc, xref)
                if pix.n > 4:
                    pix = fitz.Pixmap(fitz.csRGB, pix)
                w, h = pix.width, pix.height
                if w * h < 90000 or min(w, h) < 120:
                    continue
                if max(w, h) / max(min(w, h), 1) > 12:
                    continue
                png = pix.tobytes("png")
                digest = hashlib.sha1(png).hexdigest()
                img = _pil_from_pixmap(pix)
                if img is None:
                    continue
                if max(img.size) < 900:
                    from PIL import Image as _I

                    img = img.resize((img.width * 2, img.height * 2), _I.LANCZOS)
                LOG.info("  OCR embedded image on page %d (%dx%d)...", index + 1, w, h)
                txt = self.ocr.image_to_text(img, digest)
                if len(txt.strip()) >= 20:
                    parts.append(txt.strip())
                    n += 1
            except Exception as e:
                LOG.debug("  embedded image skipped on page %d: %s", index + 1, e)
        return ("\n\n".join(parts), n)


# ==========================================================================
# TABLES
# ==========================================================================
class PdfTableExtractor:
    """Geometry from PyMuPDF, text from word boxes.

    find_tables() finds table structure well (32 tables in 1.8s on a 28-page
    fee schedule) but its cell text comes back character-reversed for Arabic:
    'رقم الصفحة' arrives as 'ة حفصلا مقر'. The word boxes on the same page are
    in correct logical order, so we keep the geometry and re-read every cell
    from the words whose centre falls inside it, sorted right-to-left.
    """

    def __init__(self, repair: bool = False, min_rows: int = 2, min_cols: int = 2):
        self.repair = repair
        self.min_rows = min_rows
        self.min_cols = min_cols
        self.repairs_applied = 0

    def _cell_text(self, words: Sequence[tuple], bbox) -> str:
        if not bbox:
            return ""
        x0, y0, x1, y1 = bbox
        inside = []
        for w in words:
            wx0, wy0, wx1, wy1, txt = w[0], w[1], w[2], w[3], w[4]
            cx, cy = (wx0 + wx1) / 2.0, (wy0 + wy1) / 2.0
            if x0 <= cx <= x1 and y0 <= cy <= y1:
                inside.append((wy0, wx0, wx1, txt))
        if not inside:
            return ""
        # group into visual lines, then order each line right-to-left
        inside.sort(key=lambda t: t[0])
        lines: List[List[tuple]] = []
        for item in inside:
            if lines and abs(item[0] - lines[-1][0][0]) <= 4.0:
                lines[-1].append(item)
            else:
                lines.append([item])
        out_lines = []
        for ln in lines:
            ln.sort(key=lambda t: t[2], reverse=True)   # descending x = RTL
            out_lines.append(" ".join(t[3] for t in ln))
        text = " ".join(out_lines).strip()
        if self.repair:
            text, n = repair_ligatures(text)
            self.repairs_applied += n
        return text

    def extract(self, path: str) -> List[Table]:
        tables: List[Table] = []
        doc = open_pdf(path)
        try:
            for page in doc:
                try:
                    words = page.get_text("words")
                    found = page.find_tables()
                except Exception as e:
                    LOG.debug("find_tables failed on page %d: %s", page.number + 1, e)
                    continue

                for idx, t in enumerate(found.tables, 1):
                    try:
                        grid: List[List[str]] = []
                        for row in t.rows:
                            grid.append([self._cell_text(words, c) for c in row.cells])
                        grid = [r for r in grid if any(c.strip() for c in r)]
                        if len(grid) < self.min_rows:
                            continue
                        if max(len(r) for r in grid) < self.min_cols:
                            continue
                        # A "table" whose cells are all long prose is a
                        # mis-detected paragraph, not a table.
                        flat = [c for r in grid for c in r if c.strip()]
                        if not flat:
                            continue
                        # a genuine cell is short; one runaway cell means
                        # the "table" is a paragraph find_tables boxed
                        if max(len(c.split()) for c in flat) > 40:
                            continue
                        if sum(len(c.split()) for c in flat) / len(flat) > 18:
                            continue

                        headers, rows = grid[0], grid[1:]
                        if not rows:
                            continue
                        tables.append(
                            Table(
                                id="table_p%d_%d" % (page.number + 1, idx),
                                headers=headers,
                                rows=rows,
                                caption=self._caption_for(page, t.bbox),
                                page_number=page.number + 1,
                                source="pymupdf-find_tables+rtl-words",
                            )
                        )
                    except Exception as e:
                        LOG.debug("table %d on page %d skipped: %s", idx, page.number + 1, e)
        finally:
            doc.close()
        if self.repairs_applied:
            LOG.info("  table cells: %d ligature repairs applied", self.repairs_applied)
        LOG.info("  tables: %d", len(tables))
        return tables

    def _caption_for(self, page, bbox) -> str:
        """Nearest short line of text above the table, if any."""
        try:
            x0, y0, x1, y1 = bbox
            band = fitz.Rect(x0 - 20, max(0, y0 - 46), x1 + 20, y0 - 2)
            txt = page.get_textbox(band) or ""
            lines = [ln.strip() for ln in txt.split("\n") if ln.strip()]
            if lines:
                cand = lines[-1]
                if 3 <= len(cand) <= 120:
                    return cand
        except Exception:
            pass
        return ""


# ==========================================================================
# PAGE FURNITURE (running headers / footers)
# ==========================================================================
def strip_repeating_furniture(pages: List[Page], threshold: float = 0.4) -> int:
    """Drop lines that repeat near the top or bottom of most pages.

    Running titles and page numbers otherwise appear in every single chunk,
    diluting embeddings and wasting context at query time.
    """
    if len(pages) < 4:
        return 0
    from collections import Counter

    top: Counter = Counter()
    bottom: Counter = Counter()
    for p in pages:
        lines = [ln for ln in p.text.split("\n") if ln.strip()]
        for ln in lines[:3]:
            top[normalize_for_match(ln)] += 1
        for ln in lines[-3:]:
            bottom[normalize_for_match(ln)] += 1

    limit = max(3, int(len(pages) * threshold))
    banned = {k for k, v in list(top.items()) + list(bottom.items()) if v >= limit and k}
    # never drop something long enough to be real content
    banned = {b for b in banned if len(b) <= 80}
    if not banned:
        return 0

    removed = 0
    for p in pages:
        kept = []
        lines = p.text.split("\n")
        for i, ln in enumerate(lines):
            near_edge = i < 3 or i >= len(lines) - 3
            if near_edge and normalize_for_match(ln) in banned:
                removed += 1
                continue
            kept.append(ln)
        p.text = "\n".join(kept)
    LOG.info("  removed %d repeating header/footer lines", removed)
    return removed


# ==========================================================================
# SECTIONS
# ==========================================================================
ARABIC_HEADING_ANCHORS = [
    (re.compile(r"^\s*(الباب|القسم|الجزء)\s+[\w؀-ۿ]{1,20}"), 1),
    (re.compile(r"^\s*(الفصل)\s+[\w؀-ۿ]{1,20}"), 2),
    (re.compile(r"^\s*(المادة)\s*\(?\s*[\d٠-٩]{1,4}"), 3),
    (re.compile(r"^\s*(أولا|ثانيا|ثالثا|رابعا|خامسا|سادسا|سابعا|ثامنا|تاسعا|عاشرا)\s*[:\-،]?"), 2),
    (re.compile(r"^\s*(المقدمة|الخاتمة|التعريفات|المراجع|الملحق|الفهرس|الأهداف|النطاق)\s*$"), 1),
    (re.compile(r"^\s*[\d٠-٩]{1,2}[\.\-،]\s+\S.{2,80}$"), 3),
]
MD_HEADING = re.compile(r"^(#{1,6})\s+(.*\S)\s*$")


class SectionDetector:
    """Headings from markdown first, then the PDF outline, then anchored
    Arabic patterns.

    The old detector accepted any short line merely *containing* a keyword like
    المادة, so ordinary sentences became section boundaries. Anchoring the
    patterns to the start of the line stops that over-segmentation.
    """

    def __init__(self, toc: Optional[List] = None):
        self.toc = toc or []

    @staticmethod
    def _classify(line: str) -> Tuple[bool, int]:
        s = line.strip()
        if not s or len(s) > 160:
            return False, 0
        m = MD_HEADING.match(s)
        if m:
            return True, min(len(m.group(1)), 6)
        if s.endswith((".", "،", "؛", ":")) and len(s.split()) > 14:
            return False, 0
        for pat, lvl in ARABIC_HEADING_ANCHORS:
            if pat.match(s):
                return True, lvl
        return False, 0

    def detect(self, pages: List[Page], language: str) -> List[Section]:
        default_title = "المحتوى" if language in ("arabic", "mixed") else "Content"
        sections: List[Section] = []
        counter = 0
        cur: Optional[Section] = None
        buf: List[str] = []
        last_content_page = pages[0].number if pages else 0
        stack: List[Tuple[int, str, str]] = []   # (level, id, title)

        def flush(page_end: int):
            nonlocal cur, buf
            if cur is None:
                return
            body = "\n".join(buf).strip()
            cur.content = body
            cur.page_end = page_end
            cur.word_count = len(body.split())
            cur.char_count = len(body)
            cur.has_list = any(
                ln.strip().startswith(("- ", "* ", "• ")) or re.match(r"^\s*\d+[\.\)]\s", ln)
                for ln in body.split("\n")
            )
            sections.append(cur)
            cur, buf = None, []

        flat: List[Tuple[int, str]] = [
            (page.number, line) for page in pages for line in page.text.split("\n")
        ]
        list_rows = self._list_item_rows([ln for _, ln in flat])

        for idx, (page_number, line) in enumerate(flat):
            is_head, level = self._classify(line)
            if idx in list_rows:
                is_head, level = False, 0

            if is_head:
                flush(last_content_page)
                counter += 1
                title = MD_HEADING.sub(r"\2", line.strip()).strip(" •-–—:،")
                while stack and stack[-1][0] >= level:
                    stack.pop()
                parent = stack[-1][1] if stack else None
                path = [s[2] for s in stack] + [title]
                sid = "section_%03d" % counter
                cur = Section(
                    id=sid, title=title, content="", level=level,
                    page_start=page_number, page_end=page_number,
                    parent_id=parent, heading_path=path,
                )
                stack.append((level, sid, title))
                buf = []
            else:
                if cur is None:
                    counter += 1
                    cur = Section(
                        id="section_%03d" % counter, title=default_title, content="",
                        level=1, page_start=page_number, page_end=page_number,
                        heading_path=[default_title],
                    )
                    buf = []
                if line.strip():
                    buf.append(line.rstrip())
                    last_content_page = page_number

        flush(last_content_page)
        sections = self._absorb_empty(sections)
        LOG.info("  sections: %d", len(sections))
        return sections

    @staticmethod
    def _list_item_rows(lines: Sequence[str]) -> set:
        """Find numbered lines that belong to a list rather than being headings.

        '1- إيداع تحفظي' looks exactly like a numbered heading in isolation.
        What distinguishes a list is that several such lines cluster together,
        so a run of three or more within a short window is treated as content.
        Without this a numbered list is shredded into empty sections and its
        text disappears from the chunks entirely.
        """
        numbered = re.compile(r"^\s*[\d٠-٩]{1,2}\s*[\.\-\)،]\s*\S")
        hits = [i for i, ln in enumerate(lines) if numbered.match(ln or "")]
        if len(hits) < 3:
            return set()
        out: set = set()
        run: List[int] = []
        for i in hits:
            if run and i - run[-1] <= 4:
                run.append(i)
            else:
                if len(run) >= 3:
                    out.update(run)
                run = [i]
        if len(run) >= 3:
            out.update(run)
        return out

    @staticmethod
    def _absorb_empty(sections: List[Section]) -> List[Section]:
        """A heading with no body must not vanish.

        Its title is still information ('إخلاء سبيل من النيابة بضمان مالي'),
        so fold it into the next section's content rather than dropping it.
        """
        out: List[Section] = []
        pending: List[str] = []
        for s in sections:
            if not s.content.strip():
                if s.title.strip():
                    pending.append(s.title.strip())
                continue
            if pending:
                s.content = "\n".join(pending) + "\n" + s.content
                s.word_count = len(s.content.split())
                s.char_count = len(s.content)
                pending = []
            out.append(s)
        if pending and out:
            out[-1].content = out[-1].content + "\n" + "\n".join(pending)
            out[-1].word_count = len(out[-1].content.split())
            out[-1].char_count = len(out[-1].content)
        elif pending:
            out.append(Section(id="section_001", title=pending[0],
                               content="\n".join(pending), level=1))
        return out


# ==========================================================================
# CHUNKING
# ==========================================================================
SENT_SPLIT = re.compile(r"(?<=[\.\!\?؟۔])\s+|\n{2,}")


class Chunker:
    """Token-budgeted, sentence-aware chunking.

    Two changes that matter for retrieval quality:
      * every chunk carries its heading path, not just the first part of a
        section, so parts 2..n are no longer context-free;
      * splits land on sentence boundaries instead of mid-sentence word counts.
    """

    def __init__(self, max_tokens: int = 450, overlap_tokens: int = 60, min_tokens: int = 12):
        self.max_tokens = max_tokens
        self.overlap_tokens = max(0, min(overlap_tokens, max_tokens // 2))
        self.min_tokens = min_tokens
        self._n = 0

    def _next_id(self) -> str:
        self._n += 1
        return "chunk_%04d" % self._n

    @staticmethod
    def _breadcrumb(section: Section) -> str:
        path = [p for p in (section.heading_path or []) if p]
        return " > ".join(path) if path else section.title

    def _emit(self, section: Section, body: str, part: int, total: int,
              doc_id: str, source_file: str) -> Optional[Chunk]:
        body = body.strip()
        if not body:
            return None
        crumb = self._breadcrumb(section)
        content = (crumb + "\n\n" + body) if crumb else body
        if estimate_tokens(content) < self.min_tokens:
            return None
        return Chunk(
            chunk_id=self._next_id(),
            content=content,
            metadata={
                "doc_id": doc_id,
                "source_file": source_file,
                "section_id": section.id,
                "section_title": section.title,
                "heading_path": section.heading_path,
                "level": section.level,
                "page_start": section.page_start,
                "page_end": section.page_end,
                "part": part,
                "total_parts": total,
                "type": "section" if total == 1 else "section_part",
                "word_count": len(body.split()),
                "token_estimate": estimate_tokens(content),
            },
        )

    def chunk_sections(self, sections: List[Section], doc_id: str, source_file: str) -> List[Chunk]:
        chunks: List[Chunk] = []
        for section in sections:
            body = section.content.strip()
            if not body:
                continue
            if estimate_tokens(body) <= self.max_tokens:
                c = self._emit(section, body, 1, 1, doc_id, source_file)
                if c:
                    chunks.append(c)
                continue

            units = [u.strip() for u in SENT_SPLIT.split(body) if u and u.strip()]
            groups: List[List[str]] = []
            cur: List[str] = []
            cur_tok = 0
            for u in units:
                ut = estimate_tokens(u)
                if cur and cur_tok + ut > self.max_tokens:
                    groups.append(cur)
                    # sentence-level overlap, so the seam keeps its context
                    back: List[str] = []
                    back_tok = 0
                    for prev in reversed(cur):
                        pt = estimate_tokens(prev)
                        if back_tok + pt > self.overlap_tokens:
                            break
                        back.insert(0, prev)
                        back_tok += pt
                    cur, cur_tok = list(back), back_tok
                cur.append(u)
                cur_tok += ut
            if cur:
                groups.append(cur)

            total = len(groups)
            for i, g in enumerate(groups, 1):
                c = self._emit(section, " ".join(g), i, total, doc_id, source_file)
                if c:
                    chunks.append(c)
        return chunks

    def chunk_tables(self, tables: List[Table], sections_by_id: Dict[str, Section],
                     doc_id: str, source_file: str) -> List[Chunk]:
        """One chunk per table (for display) plus one per row (for answering).

        Row chunks are what let 'كم رسم نقل ملكية البراءة؟' hit a single vector
        that contains both the label and the number.
        """
        out: List[Chunk] = []
        for t in tables:
            sec = sections_by_id.get(t.section_id)
            crumb = " > ".join(sec.heading_path) if sec and sec.heading_path else (t.caption or "")
            base_meta = {
                "doc_id": doc_id,
                "source_file": source_file,
                "section_id": t.section_id,
                "section_title": sec.title if sec else "",
                "heading_path": sec.heading_path if sec else [],
                "table_id": t.id,
                "table_caption": t.caption,
                "page_start": t.page_number,
                "page_end": t.page_number,
            }

            md = t.to_markdown()
            if md:
                header = (crumb + "\n\n") if crumb else ""
                meta = dict(base_meta)
                meta.update({"type": "table", "token_estimate": estimate_tokens(md)})
                out.append(Chunk(self._next_id(), header + md, meta))

            for i, sentence in enumerate(t.row_sentences(), 1):
                text = (crumb + "\n" + sentence) if crumb else sentence
                if estimate_tokens(text) < self.min_tokens:
                    continue
                meta = dict(base_meta)
                meta.update({"type": "table_row", "row_index": i,
                             "token_estimate": estimate_tokens(text)})
                out.append(Chunk(self._next_id(), text, meta))
        return out


# ==========================================================================
# NON-PDF FORMATS
# ==========================================================================
PDF_EXT = {".pdf"}
DOCX_EXT = {".docx", ".docm"}
XLSX_EXT = {".xlsx", ".xlsm", ".xls"}
PPTX_EXT = {".pptx", ".pptm"}
HTML_EXT = {".html", ".htm", ".xhtml"}
IMAGE_EXT = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".gif"}
TEXT_EXT = {".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".log", ".rtf"}
SUPPORTED = PDF_EXT | DOCX_EXT | XLSX_EXT | PPTX_EXT | HTML_EXT | IMAGE_EXT | TEXT_EXT


def _decode_bytes(raw: bytes) -> str:
    for enc in ("utf-8-sig", "utf-8"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            pass
    if chardet is not None:
        try:
            guess = chardet.detect(raw)
            if guess and guess.get("encoding"):
                return raw.decode(guess["encoding"], errors="replace")
        except Exception:
            pass
    # Arabic Windows legacy encoding, then a lossy last resort
    for enc in ("cp1256", "latin-1"):
        try:
            return raw.decode(enc)
        except Exception:
            pass
    return raw.decode("utf-8", errors="replace")


class DocxHandler:
    """python-docx, in document order, preserving headings and tables.

    Replaces the C# ExtractDocxText, which called Docnet (a PDFium wrapper) on
    a .docx -- it always threw and returned an empty string, silently.
    """

    def __init__(self, ocr: Optional[SuryaOCR] = None):
        self.ocr = ocr

    def extract(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        if docx_mod is None:
            raise RuntimeError("python-docx is not installed: pip install python-docx")
        from docx import Document as _Docx
        from docx.table import Table as _DTable
        from docx.text.paragraph import Paragraph as _DPara

        d = _Docx(path)
        lines: List[str] = []
        tables: List[Table] = []
        body = d.element.body
        t_index = 0

        for child in body.iterchildren():
            tag = child.tag.split("}")[-1]
            if tag == "p":
                para = _DPara(child, d)
                text = (para.text or "").strip()
                if not text:
                    continue
                style = (para.style.name or "") if para.style is not None else ""
                level = 0
                m = re.match(r"Heading (\d+)", style)
                if m:
                    level = int(m.group(1))
                elif style.lower() in ("title", "subtitle"):
                    level = 1
                if level:
                    lines.append("#" * min(level, 6) + " " + text)
                else:
                    lines.append(text)
            elif tag == "tbl":
                t_index += 1
                dt = _DTable(child, d)
                grid = [[(c.text or "").strip() for c in row.cells] for row in dt.rows]
                grid = [r for r in grid if any(x for x in r)]
                if len(grid) >= 2:
                    tables.append(
                        Table(
                            id="table_docx_%d" % t_index,
                            headers=grid[0],
                            rows=grid[1:],
                            caption="",
                            page_number=1,
                            source="docx-native",
                        )
                    )
                    lines.append("")
                    lines.append(tables[-1].to_markdown())
                    lines.append("")

        ocr_n = 0
        if self.ocr is not None:
            text, ocr_n = self._ocr_images(d)
            if text:
                lines.append("")
                lines.append(text)

        page = Page(number=1, text="\n".join(lines), source="native", ocr_images=ocr_n)
        meta = {"backend": "python-docx", "ocr_embedded_images": ocr_n,
                "docx_tables": len(tables)}
        return [page], tables, meta

    def _ocr_images(self, d) -> Tuple[str, int]:
        parts: List[str] = []
        n = 0
        try:
            from PIL import Image as _I
        except Exception:
            return "", 0
        for rel in list(d.part.rels.values()):
            try:
                if "image" not in rel.reltype:
                    continue
                blob = rel.target_part.blob
                if len(blob) < 8000:
                    continue
                img = _I.open(io.BytesIO(blob)).convert("RGB")
                if img.width * img.height < 90000 or min(img.size) < 120:
                    continue
                if max(img.size) < 900:
                    img = img.resize((img.width * 2, img.height * 2), _I.LANCZOS)
                txt = self.ocr.image_to_text(img, hashlib.sha1(blob).hexdigest())
                if len(txt.strip()) >= 20:
                    parts.append(txt.strip())
                    n += 1
            except Exception:
                continue
        return "\n\n".join(parts), n


class XlsxHandler:
    """One page per worksheet; each sheet also becomes a real Table.

    The C# path concatenated every cell space-separated with no row or column
    structure, which makes a spreadsheet unanswerable.
    """

    def extract(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        ext = Path(path).suffix.lower()
        sheets: List[Tuple[str, List[List[str]]]] = []

        if ext == ".xls":
            import pandas as pd

            book = pd.read_excel(path, sheet_name=None, header=None, dtype=str)
            for name, df in book.items():
                grid = [["" if v is None or str(v) == "nan" else str(v).strip()
                         for v in row] for row in df.values.tolist()]
                sheets.append((name, grid))
        else:
            if openpyxl is None:
                raise RuntimeError("openpyxl is not installed")
            wb = openpyxl.load_workbook(path, data_only=True, read_only=True)
            try:
                for ws in wb.worksheets:
                    grid = []
                    for row in ws.iter_rows(values_only=True):
                        grid.append(["" if v is None else str(v).strip() for v in row])
                    sheets.append((ws.title, grid))
            finally:
                wb.close()

        pages: List[Page] = []
        tables: List[Table] = []
        for i, (name, grid) in enumerate(sheets, 1):
            grid = [r for r in grid if any(c for c in r)]
            if not grid:
                continue
            t = Table(
                id="table_sheet_%d" % i,
                headers=grid[0],
                rows=grid[1:],
                caption=name,
                page_number=i,
                source="openpyxl-native",
            )
            if t.rows:
                tables.append(t)
            pages.append(Page(number=i, text="# " + name + "\n\n" + t.to_markdown(),
                              source="native"))
        meta = {"backend": "openpyxl", "sheets": len(pages)}
        return pages, tables, meta


class PptxHandler:
    """One page per slide, plus speaker notes and OCR of picture shapes.

    Slide decks are mostly text-in-images, so the OCR path matters more here
    than anywhere else.
    """

    def __init__(self, ocr: Optional[SuryaOCR] = None):
        self.ocr = ocr

    def extract(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        if pptx_mod is None:
            raise RuntimeError("python-pptx is not installed: pip install python-pptx")
        from pptx import Presentation

        prs = Presentation(path)
        pages: List[Page] = []
        tables: List[Table] = []
        ocr_total = 0

        for i, slide in enumerate(prs.slides, 1):
            lines: List[str] = []
            title = ""
            try:
                if slide.shapes.title is not None and slide.shapes.title.has_text_frame:
                    title = (slide.shapes.title.text or "").strip()
            except Exception:
                pass
            lines.append("# " + (title if title else "شريحة %d" % i))

            ocr_n = 0
            for shape in slide.shapes:
                try:
                    if shape.has_text_frame:
                        txt = (shape.text or "").strip()
                        if txt and txt != title:
                            lines.append(txt)
                    if getattr(shape, "has_table", False):
                        grid = [[(c.text or "").strip() for c in row.cells]
                                for row in shape.table.rows]
                        grid = [r for r in grid if any(x for x in r)]
                        if len(grid) >= 2:
                            t = Table(
                                id="table_slide%d_%d" % (i, len(tables) + 1),
                                headers=grid[0], rows=grid[1:], caption=title,
                                page_number=i, source="pptx-native",
                            )
                            tables.append(t)
                            lines.append("")
                            lines.append(t.to_markdown())
                    if self.ocr is not None and shape.shape_type is not None and \
                            "PICTURE" in str(shape.shape_type):
                        txt = self._ocr_picture(shape)
                        if txt:
                            lines.append(txt)
                            ocr_n += 1
                except Exception:
                    continue

            try:
                if slide.has_notes_slide:
                    note = (slide.notes_slide.notes_text_frame.text or "").strip()
                    if note:
                        lines.append("")
                        lines.append("ملاحظات: " + note)
            except Exception:
                pass

            ocr_total += ocr_n
            pages.append(Page(number=i, text="\n".join(lines),
                              source="native" if not ocr_n else "native+ocr-embedded",
                              ocr_images=ocr_n))

        meta = {"backend": "python-pptx", "slides": len(pages),
                "ocr_embedded_images": ocr_total}
        return pages, tables, meta

    def _ocr_picture(self, shape) -> str:
        try:
            from PIL import Image as _I

            blob = shape.image.blob
            if len(blob) < 8000:
                return ""
            img = _I.open(io.BytesIO(blob)).convert("RGB")
            if img.width * img.height < 90000 or min(img.size) < 120:
                return ""
            if max(img.size) < 900:
                img = img.resize((img.width * 2, img.height * 2), _I.LANCZOS)
            txt = self.ocr.image_to_text(img, hashlib.sha1(blob).hexdigest())
            return txt.strip() if len(txt.strip()) >= 20 else ""
        except Exception:
            return ""


class HtmlHandler:
    def extract(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        raw = Path(path).read_bytes()
        html = _decode_bytes(raw)
        if bs4 is None:
            text = re.sub(r"<[^>]+>", " ", html)
            return [Page(1, text, "native")], [], {"backend": "regex-strip"}
        from bs4 import BeautifulSoup

        soup = BeautifulSoup(html, "lxml" if _try("lxml") else "html.parser")
        for tag in soup(["script", "style", "noscript"]):
            tag.decompose()

        tables: List[Table] = []
        for i, tb in enumerate(soup.find_all("table"), 1):
            grid = []
            for tr in tb.find_all("tr"):
                cells = [c.get_text(" ", strip=True) for c in tr.find_all(["th", "td"])]
                if any(cells):
                    grid.append(cells)
            if len(grid) >= 2:
                tables.append(Table("table_html_%d" % i, grid[0], grid[1:],
                                    "", page_number=1, source="html-native"))
            tb.decompose()

        lines: List[str] = []
        for el in soup.find_all(["h1", "h2", "h3", "h4", "h5", "h6", "p", "li"]):
            txt = el.get_text(" ", strip=True)
            if not txt:
                continue
            if el.name.startswith("h") and len(el.name) == 2 and el.name[1].isdigit():
                lines.append("#" * int(el.name[1]) + " " + txt)
            elif el.name == "li":
                lines.append("- " + txt)
            else:
                lines.append(txt)
        if not lines:
            lines = [soup.get_text("\n", strip=True)]
        for t in tables:
            lines.append("")
            lines.append(t.to_markdown())
        return [Page(1, "\n".join(lines), "native")], tables, {"backend": "beautifulsoup"}


class ImageHandler:
    def __init__(self, ocr: Optional[SuryaOCR] = None):
        self.ocr = ocr

    def extract(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        if self.ocr is None:
            LOG.warning("image input but OCR is disabled -- no text will be produced")
            return [Page(1, "", "ocr")], [], {"backend": "none"}
        from PIL import Image as _I

        raw = Path(path).read_bytes()
        img = _I.open(io.BytesIO(raw)).convert("RGB")
        if max(img.size) < 1200:
            scale = max(1, int(1200 / max(img.size)))
            if scale > 1:
                img = img.resize((img.width * scale, img.height * scale), _I.LANCZOS)
        LOG.info("  OCR image %dx%d...", img.width, img.height)
        text = self.ocr.image_to_text(img, hashlib.sha1(raw).hexdigest())
        return [Page(1, text, "ocr")], [], {"backend": "surya", "ocr_pages": 1}


class TextHandler:
    def extract(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        raw = Path(path).read_bytes()
        text = _decode_bytes(raw)
        ext = Path(path).suffix.lower()
        tables: List[Table] = []

        if ext in (".csv", ".tsv"):
            import csv as _csv

            delim = "\t" if ext == ".tsv" else ","
            try:
                rows = [r for r in _csv.reader(io.StringIO(text), delimiter=delim) if any(r)]
                if len(rows) >= 2:
                    t = Table("table_csv_1", rows[0], rows[1:], Path(path).stem,
                              page_number=1, source="csv-native")
                    tables.append(t)
                    text = t.to_markdown()
            except Exception:
                pass
        elif ext == ".json":
            try:
                text = json.dumps(json.loads(text), ensure_ascii=False, indent=2)
            except Exception:
                pass
        elif ext == ".rtf":
            # no striprtf here; drop control words so the words survive
            text = re.sub(r"\\[a-zA-Z]+-?\d* ?", " ", text)
            text = text.replace("{", " ").replace("}", " ")

        return [Page(1, text, "native")], tables, {"backend": "text"}


# ==========================================================================
# ORCHESTRATOR
# ==========================================================================
def detect_language(text: str) -> Tuple[str, float]:
    arabic = len(re.findall(rf"[{ARABIC_RANGE}]", text))
    latin = len(re.findall(r"[A-Za-z]", text))
    total = arabic + latin
    if total == 0:
        return "unknown", 0.0
    ratio = arabic / total
    if ratio >= 0.6:
        return "arabic", ratio
    if ratio >= 0.15:
        return "mixed", ratio
    return "english", 1.0 - ratio


class UniversalExtractor:
    def __init__(
        self,
        ocr_enabled: bool = True,
        ocr_embedded: bool = True,
        ocr_on_lossy: bool = False,
        verify_lossy: bool = False,
        ocr_dpi: int = 200,
        max_tokens: int = 450,
        overlap_tokens: int = 60,
        strip_diacritics: bool = True,
        extract_tables: bool = True,
        cache_dir: Optional[Path] = None,
    ):
        self.ocr_enabled = ocr_enabled
        self.extract_tables = extract_tables
        self.normalizer = ArabicNormalizer(strip_diacritics=strip_diacritics)
        self.chunker_opts = (max_tokens, overlap_tokens)
        self.ocr = SuryaOCR.get(cache_dir) if ocr_enabled else None
        self.pdf = PdfExtractor(self.ocr, ocr_enabled, ocr_dpi,
                                ocr_embedded=ocr_embedded, ocr_on_lossy=ocr_on_lossy,
                                verify_lossy=verify_lossy)

    # -- dispatch --------------------------------------------------------
    def _read_source(self, path: str) -> Tuple[List[Page], List[Table], Dict]:
        ext = Path(path).suffix.lower()
        if ext in PDF_EXT:
            pages, meta = self.pdf.extract_pages(path)
            tables: List[Table] = []
            if self.extract_tables:
                damaged = meta.get("pymupdf_damage_ratio", 0.0) > 0.25
                tables = PdfTableExtractor(repair=damaged).extract(path)
            return pages, tables, meta
        if ext in DOCX_EXT:
            return DocxHandler(self.ocr).extract(path)
        if ext in XLSX_EXT:
            return XlsxHandler().extract(path)
        if ext in PPTX_EXT:
            return PptxHandler(self.ocr).extract(path)
        if ext in HTML_EXT:
            return HtmlHandler().extract(path)
        if ext in IMAGE_EXT:
            return ImageHandler(self.ocr).extract(path)
        if ext in TEXT_EXT:
            return TextHandler().extract(path)
        raise ValueError("unsupported file type: " + ext)

    # -- main ------------------------------------------------------------
    def extract(self, path: str) -> Document:
        src = Path(path)
        t_start = time.time()
        LOG.info("=" * 68)
        LOG.info("Processing: %s", src.name)

        pages, tables, backend_meta = self._read_source(str(src))

        strip_repeating_furniture(pages)

        for p in pages:
            p.text = self.normalizer.clean(p.text)
        pages = [p for p in pages if p.text.strip()] or pages

        full_text = "\n\n".join(p.text for p in pages).strip()
        language, confidence = detect_language(full_text)
        LOG.info("  language: %s (%.0f%%)", language, confidence * 100)

        sections = SectionDetector(backend_meta.get("toc")).detect(pages, language)
        if not sections:
            sections = [
                Section(
                    id="section_001",
                    title="المحتوى" if language in ("arabic", "mixed") else "Content",
                    content=full_text, level=1,
                    page_start=pages[0].number if pages else 1,
                    page_end=pages[-1].number if pages else 1,
                    heading_path=["المحتوى" if language in ("arabic", "mixed") else "Content"],
                )
            ]

        # clean table cells with the same pipeline as body text
        for t in tables:
            t.headers = [self.normalizer.clean(h) for h in t.headers]
            t.rows = [[self.normalizer.clean(c) for c in r] for r in t.rows]
            t.caption = self.normalizer.clean(t.caption)

        self._link_tables(tables, sections)

        doc_id = hashlib.sha1(str(src.resolve()).encode("utf-8")).hexdigest()[:12]
        chunker = Chunker(self.chunker_opts[0], self.chunker_opts[1])
        chunks = chunker.chunk_sections(sections, doc_id, str(src))
        if tables:
            by_id = {s.id: s for s in sections}
            chunks += chunker.chunk_tables(tables, by_id, doc_id, str(src))
        chunks = self._dedupe(chunks)

        elapsed = time.time() - t_start
        meta: Dict[str, Any] = {
            "doc_id": doc_id,
            "source_file": str(src),
            "file_name": src.name,
            "file_ext": src.suffix.lower(),
            "file_size_mb": round(src.stat().st_size / (1024 * 1024), 3),
            "extraction_date": datetime.now().isoformat(timespec="seconds"),
            "extractor_version": "universal-1.0",
            "language": language,
            "language_confidence": round(confidence, 4),
            "elapsed_seconds": round(elapsed, 2),
            "max_tokens": self.chunker_opts[0],
            "overlap_tokens": self.chunker_opts[1],
        }
        meta.update(backend_meta)
        meta.pop("toc", None)

        doc = Document(
            title=self._title(src, pages, backend_meta),
            language=language,
            source_file=str(src),
            pages=pages,
            sections=sections,
            tables=tables,
            chunks=chunks,
            full_text=full_text,
            metadata=meta,
        )
        st = doc.stats()
        LOG.info(
            "  DONE in %.1fs -- %d pages (%d OCR), %d sections, %d tables, %d chunks (%d table rows)",
            elapsed, st["total_pages"], st["ocr_pages"], st["total_sections"],
            st["total_tables"], st["total_chunks"], st["table_chunks"],
        )
        return doc

    @staticmethod
    def _link_tables(tables: List[Table], sections: List[Section]) -> None:
        for t in tables:
            best: Optional[Section] = None
            for s in sections:
                if s.page_start <= t.page_number <= s.page_end:
                    best = s
                    break
            if best is None and sections:
                best = min(sections, key=lambda s: abs(s.page_start - t.page_number))
            if best is not None:
                t.section_id = best.id
                best.has_table = True

    @staticmethod
    def _dedupe(chunks: List[Chunk]) -> List[Chunk]:
        seen: set = set()
        out: List[Chunk] = []
        for c in chunks:
            key = hashlib.sha1(normalize_for_match(c.content).encode("utf-8")).hexdigest()
            if key in seen:
                continue
            seen.add(key)
            out.append(c)
        dropped = len(chunks) - len(out)
        if dropped:
            LOG.info("  removed %d duplicate chunks", dropped)
        return out

    @staticmethod
    def _title(src: Path, pages: List[Page], meta: Dict) -> str:
        pdf_meta = meta.get("pdf_metadata") or {}
        t = (pdf_meta.get("title") or "").strip()
        if t and len(t) > 3 and not t.lower().endswith((".pdf", ".docx", ".indd")):
            return t
        for p in pages[:2]:
            for line in p.text.split("\n"):
                s = line.strip().lstrip("#").strip()
                if 8 <= len(s) <= 120:
                    return s
        return src.stem


# ==========================================================================
# OUTPUT
# ==========================================================================
def save_outputs(doc: Document, out_dir: Path, write_jsonl: bool = False) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    base = Path(doc.metadata["source_file"]).stem

    def w(name: str, payload: Any, as_json: bool = True) -> Path:
        p = out_dir / name
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            if as_json:
                json.dump(payload, f, ensure_ascii=False, indent=2)
            else:
                f.write(payload)
        return p

    # 1. flat text -- this is the file Extractor.cs reads today. Keeping it
    #    identical in name and location is what makes this a drop-in change.
    #    The detected sections are written back as markdown headings. The C#
    #    chunker (ImprovedSemanticChunker.DetectSectionWithLevel) recognises
    #    "#"-prefixed lines, but its Arabic-only heuristics found headings in just
    #    one of five documents. Emitting the headings this extractor already
    #    detected lets it label chunks with their section in all of them -- which
    #    is what puts the section into the embedded text.
    text_out = doc.full_text
    if doc.sections and any(s.title.strip() for s in doc.sections):
        parts: List[str] = []
        for s in doc.sections:
            title = (s.title or "").strip()
            if title and title not in ("المحتوى", "Content"):
                parts.append("#" * max(1, min(s.level, 6)) + " " + title)
                parts.append("")
            body = (s.content or "").strip()
            if body:
                parts.append(body)
                parts.append("")
        rebuilt = "\n".join(parts).strip()
        # never let the restructure lose text
        if len(rebuilt) >= len(doc.full_text) * 0.95:
            text_out = rebuilt
        else:
            LOG.warning("  section rebuild lost text (%d -> %d chars); keeping flat text",
                        len(doc.full_text), len(rebuilt))

    if doc.tables:
        parts = [text_out, "", "=" * 60, "الجداول المستخرجة" if doc.language != "english" else "EXTRACTED TABLES", "=" * 60]
        for t in doc.tables:
            parts.append("")
            parts.append("[%s | صفحة %d]" % (t.caption or t.id, t.page_number))
            parts.append(t.to_markdown())
        text_out = "\n".join(parts)
    w(base + "_clean.txt", text_out, as_json=False)

    # 2. structured outputs for the staged migration
    w(base + "_chunks.json", [c.to_dict() for c in doc.chunks])
    if write_jsonl:
        with open(out_dir / (base + "_chunks.jsonl"), "w", encoding="utf-8",
                  newline="\n") as f:
            for c in doc.chunks:
                f.write(json.dumps(c.to_dict(), ensure_ascii=False) + "\n")
    w(base + "_sections.json", [s.to_dict() for s in doc.sections])
    w(base + "_pages.json", [p.to_dict() for p in doc.pages])
    w(base + "_full.json", doc.to_dict())
    w(base + "_stats.json", {**doc.stats(), **doc.metadata})

    if doc.tables:
        w(base + "_tables.json", [t.to_dict() for t in doc.tables])
        md = []
        for t in doc.tables:
            md.append("## %s (صفحة %d)" % (t.caption or t.id, t.page_number))
            md.append("")
            md.append(t.to_markdown())
            md.append("")
            md.append("---")
        w(base + "_tables.md", "\n".join(md), as_json=False)
        csv_dir = out_dir / (base + "_tables_csv")
        csv_dir.mkdir(exist_ok=True)
        for t in doc.tables:
            (csv_dir / (t.id + ".csv")).write_text(t.to_csv(), encoding="utf-8-sig")

    LOG.info("  wrote %d files to %s", len(list(out_dir.glob(base + "_*"))), out_dir)


# ==========================================================================
# CLI
# ==========================================================================
# Directories and filenames this pipeline produces. Without these guards a
# folder run re-ingests its own previous output: pointing it at Uploads/ found
# 458 "documents", almost all of them extracts of the seven real ones.
SKIP_DIRS = {"extracted", ".ocr_cache", "__pycache__", "results", "results_all",
             "rerank_results", "bin", "obj", "node_modules", ".git"}
OUTPUT_SUFFIXES = ("_clean.txt", "_chunks.json", "_chunks.jsonl", "_sections.json",
                   "_pages.json", "_tables.json", "_stats.json", "_full.json",
                   "_tables.md")


def _is_input_file(p: Path, out_root: Path) -> bool:
    if not p.is_file() or p.suffix.lower() not in SUPPORTED:
        return False
    if any(part.lower() in SKIP_DIRS for part in p.parts):
        return False
    if p.name.endswith(OUTPUT_SUFFIXES):
        return False
    try:
        if out_root.resolve() in p.resolve().parents:
            return False
    except Exception:
        pass
    return True


def process_one(path: Path, out_dir: Path, extractor: UniversalExtractor,
                write_jsonl: bool = False) -> bool:
    try:
        doc = extractor.extract(str(path))
        save_outputs(doc, out_dir, write_jsonl=write_jsonl)
        return True
    except Exception as e:
        LOG.error("FAILED %s: %s", path.name, e)
        LOG.debug("", exc_info=True)
        return False


def main(argv: Optional[List[str]] = None) -> int:
    import argparse

    ap = argparse.ArgumentParser(
        description="Universal Arabic-first document extractor for RAG",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Formats: pdf, docx, xlsx/xls, pptx, html, png/jpg/tiff/..., txt/csv/json/md

  python extract_universal.py input.pdf  out_dir/
  python extract_universal.py Uploads/   out_dir/  --skip-existing
  python extract_universal.py scan.pdf   out_dir/  --ocr-dpi 300
  python extract_universal.py fast.pdf   out_dir/  --no-ocr --no-tables
""",
    )
    ap.add_argument("input", help="file or directory")
    ap.add_argument("output", help="output directory")
    ap.add_argument("--max-tokens", type=int, default=450,
                    help="target chunk size in tokens (default 450)")
    ap.add_argument("--overlap-tokens", type=int, default=60)
    ap.add_argument("--no-tables", action="store_true")
    ap.add_argument("--no-ocr", action="store_true",
                    help="never OCR, even for pages with no text layer")
    ap.add_argument("--no-ocr-embedded", action="store_true",
                    help="OCR text-less pages but skip images inside readable pages")
    ap.add_argument("--verify-lossy", action="store_true",
                    help="confirm a suspected lossy text layer by OCR-ing one page "
                         "(costs minutes on CPU; the frequency test alone is free)")
    ap.add_argument("--ocr-on-lossy-text", action="store_true",
                    help="if the PDF fonts drop ligatures (silent character loss), "
                         "OCR every page instead of trusting the text layer")
    ap.add_argument("--ocr-dpi", type=int, default=200)
    ap.add_argument("--keep-diacritics", action="store_true")
    ap.add_argument("--per-file-dirs", action="store_true",
                    help="write each document into <output>/<stem>/")
    ap.add_argument("--skip-existing", action="store_true")
    ap.add_argument("--jsonl", action="store_true",
                    help="also write <name>_chunks.jsonl (off by default: the C# "
                         "indexer scans Uploads/** and does not filter .jsonl)")
    ap.add_argument("--cache-dir", default=None, help="OCR cache (default <output>/.ocr_cache)")
    ap.add_argument("--log-file", default=None)
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    out_root = Path(args.output)
    out_root.mkdir(parents=True, exist_ok=True)
    _setup_logging(args.verbose, Path(args.log_file) if args.log_file else None)

    cache_dir = Path(args.cache_dir) if args.cache_dir else (out_root / ".ocr_cache")

    extractor = UniversalExtractor(
        ocr_enabled=not args.no_ocr,
        ocr_embedded=not args.no_ocr_embedded,
        ocr_on_lossy=args.ocr_on_lossy_text,
        verify_lossy=args.verify_lossy,
        ocr_dpi=args.ocr_dpi,
        max_tokens=args.max_tokens,
        overlap_tokens=args.overlap_tokens,
        strip_diacritics=not args.keep_diacritics,
        extract_tables=not args.no_tables,
        cache_dir=cache_dir,
    )

    in_path = Path(args.input)
    if in_path.is_file():
        targets = [in_path]
    else:
        targets = sorted(p for p in in_path.rglob("*") if _is_input_file(p, out_root))
        LOG.info("found %d supported files", len(targets))

    ok = failed = skipped = 0
    for i, p in enumerate(targets, 1):
        dest = out_root / p.stem if (args.per_file_dirs or len(targets) > 1) else out_root
        if args.skip_existing and (dest / (p.stem + "_chunks.json")).exists():
            skipped += 1
            continue
        if len(targets) > 1:
            LOG.info("[%d/%d]", i, len(targets))
        if process_one(p, dest, extractor, write_jsonl=args.jsonl):
            ok += 1
        else:
            failed += 1

    LOG.info("=" * 68)
    LOG.info("BATCH COMPLETE: %d ok, %d failed, %d skipped", ok, failed, skipped)
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    _setup_logging()
    sys.exit(main())
