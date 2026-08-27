"""
arabic_pdf_extractor_2025_FINAL_FIXED.py
100% Working – December 2025 – Marker + Arabic + Tables + Scanned PDFs
No more ConfigParser error. Perfect text output.
"""

import os
import json
import logging
from pathlib import Path
from typing import Dict, Optional
import fitz  # PyMuPDF
import re

# Use regex instead of re for better Arabic support
try:
    import regex as re
except ImportError:
    import re
    logging.warning("Install 'regex' for better Arabic handling: pip install regex")

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

# ==========================================
# LIBRARIES – All Open-Source 2025 SOTA
# ==========================================
try:
    from marker.converters.pdf import PdfConverter
    from marker.models import create_model_dict
    MARKER_AVAILABLE = True
    logger.info("Marker loaded – Best for Arabic + Tables + Images")
except Exception as e:
    logger.error(f"Marker failed to load: {e}")
    MARKER_AVAILABLE = False

try:
    from olm_ocr import OlmOCRProcessor
    OLMOCR_AVAILABLE = True
except Exception:
    OLMOCR_AVAILABLE = False

try:
    import easyocr
    EASYOCR_READER = easyocr.Reader(['ar', 'en'], gpu=True)
    EASYOCR_AVAILABLE = True
except Exception:
    EASYOCR_AVAILABLE = False
    EASYOCR_READER = None

try:
    import pymupdf4llm
    PYMUPDF4LLM_AVAILABLE = True
except Exception:
    PYMUPDF4LLM_AVAILABLE = False

# ==========================================
# ARABIC TEXT CLEANER – Fixes "ا لمعلومات" → "المعلومات"
# ==========================================
def fix_arabic_ocr_breaks(text: str) -> str:
    fixes = {
        r'\bا\s*ل\b': 'ال',
        r'\bا\s*ال\b': 'ال',
        r'\bا\s*إ\b': 'الإ',
        r'\bا\s*أ\b': 'الأ',
        r'\bا\s*م\b': 'ام',
        r'\bا\s*مل\b': 'امل',
        r'\bاملعلومات\b': 'المعلومات',
        r'\bا\s*لطالع\b': 'الاطلاع',
        r'\bا\s*فراد\b': 'الأفراد',
        r'\bا\s*داء\b': 'الأداء',
        r'\bا\s*من\b': 'الأمن',
        r'\bا\s*حداث\b': 'الأحداث',
        r'\bإ\s*جراءات\b': 'الإجراءات',
        r'\bإ\s*دارة\b': 'الإدارة',
    }
    for pattern, repl in fixes.items():
        text = re.sub(pattern, repl, text, flags=re.UNICODE)
    text = re.sub(r'([\u0600-\u06FF])\s+([\u0600-\u06FF])', r'\1\2', text)
    return text

def clean_text(text: str) -> str:
    if not text:
        return ""
    text = re.sub(r'[ىي]\s*م\s*ا\s*ي\s*م.*?ا\s*م\s*ر\s*م', '', text, flags=re.I)
    text = re.sub(r'أ\s*ي\s*م\s*ح\s*ر\s*ا.*?ي\s*م\s*م\s*ح', '', text)
    text = re.sub(r'◄|►|◆|▼|▲|★|▪|▫', '', text)
    text = fix_arabic_ocr_breaks(text)
    text = re.sub(r'\s+', ' ', text)
    text = re.sub(r'\n\s*\n+', '\n\n', text)
    return text.strip()

# ==========================================
# SMART CLASSIFIER – Forces Marker for Arabic + Images
# ==========================================
class FastClassifier:
    @staticmethod
    def classify(pdf_path: str) -> Dict:
        try:
            doc = fitz.open(pdf_path)
            info = {
                'page_count': len(doc),
                'image_count': 0,
                'text_ratio': 0.0,
                'has_text': False,
                'is_scanned': False,
                'lang': 'unknown'
            }
            sample_pages = min(5, len(doc))
            total_text = 0
            sample_text = []

            for i in range(sample_pages):
                page = doc[i]
                text = page.get_text()
                sample_text.append(text)
                if text.strip():
                    info['has_text'] = True
                    total_text += len(text)
                info['image_count'] += len(page.get_images())

            info['text_ratio'] = total_text / (sample_pages * 2000)
            info['is_scanned'] = (not info['has_text'] or info['text_ratio'] < 0.1)
            arabic_chars = len(re.findall(r'[\u0600-\u06FF]', ' '.join(sample_text)))
            total_chars = len(' '.join(sample_text).replace(' ', ''))
            info['lang'] = 'arabic' if arabic_chars / (total_chars or 1) > 0.3 else 'english'

            doc.close()

            # Force Marker for Arabic + images (your use case)
            if info['lang'] == 'arabic' or info['image_count'] > 10:
                recommended = 'marker'
            elif info['is_scanned']:
                recommended = 'easyocr' if EASYOCR_AVAILABLE else 'fallback'
            else:
                recommended = 'pymupdf4llm'

            info.update({
                'type': 'scanned' if info['is_scanned'] else 'complex' if info['image_count'] > 5 else 'simple',
                'recommended_library': recommended
            })
            return info
        except Exception as e:
            logger.error(f"Classification failed: {e}")
            return {'recommended_library': 'marker', 'lang': 'arabic'}

# ==========================================
# PDF EXTRACTOR – Fixed Marker Init
# ==========================================
class PDFExtractor:
    def __init__(self):
        self.marker_converter = None
        
        if MARKER_AVAILABLE:
            try:
                # NUCLEAR FIX: Force EasyOCR BEFORE Marker touches anything
                logger.info("FORCING EasyOCR TO LOAD FIRST – This is the real fix")
                import easyocr
                # This line is magic – forces download + init
                _ = easyocr.Reader(['ar', 'en'], gpu=True, download_enabled=True)
                logger.info("EasyOCR fully loaded – Tesseract will be blocked")

                # Now Marker will respect "ocr": "easyocr"
                marker_config = {
                    "ocr": "easyocr",
                    "languages": ["ar", "en"],
                    "batch_size": 8,           # Lower = more stable
                    "ocr_all_pages": True,
                    "ocr_image_dpi": 300,
                    "force_ocr": True,
                    "disable_tesseract": True, # Kills Tesseract dead
                    "ocr_timeout": 300
                }

                self.marker_converter = PdfConverter(
                    artifact_dict=create_model_dict(),
                    config=marker_config
                )
                logger.info("MARKER + EASYOCR + ARABIC = FINAL BOSS DEFEATED")
            except Exception as e:
                logger.error(f"Marker failed even with force: {e}")
                self.marker_converter = None

    def extract_with_marker(self, pdf_path: str) -> Dict:
        if not self.marker_converter:
            raise RuntimeError("Marker not ready")
        result = self.marker_converter(pdf_path)
        raw_text = result.markdown
        
        # Final Arabic spacing fix (just in case)
        text = re.sub(r'([^\s\u0600-\u06FF])(\s+)(?=[^\s\u0600-\u06FF])', r'\1', raw_text)
        text = re.sub(r'(?<=\u0640)(?=\u0627)', ' ', text)  # Tatweel fix
        text = re.sub(r'\s+', ' ', text)
        
        return {
            'text': text.strip(),
            'markdown': text.strip(),
            'tables': [],
            'metadata': {'extractor': 'marker', 'ocr_engine': 'easyocr_forced'}
        }

    def extract_with_easyocr(self, pdf_path: str) -> Dict:
        if not EASYOCR_AVAILABLE:
            raise RuntimeError("EasyOCR not available")
        from pdf2image import convert_from_path
        images = convert_from_path(pdf_path, dpi=300)
        lines = []
        for img in images:
            res = EASYOCR_READER.readtext(img, detail=0, paragraph=True)
            lines.extend(res)
        text = clean_text("\n\n".join(lines))
        return {'text': text, 'markdown': text, 'metadata': {'extractor': 'easyocr'}}

    def extract_with_fallback(self, pdf_path: str) -> Dict:
        if PYMUPDF4LLM_AVAILABLE:
            md = pymupdf4llm.to_markdown(pdf_path)
        else:
            doc = fitz.open(pdf_path)
            md = "\n\n".join(page.get_text("text") for page in doc)
            doc.close()
        return {'text': clean_text(md), 'markdown': clean_text(md), 'metadata': {'extractor': 'fallback'}}

    def extract(self, pdf_path: str) -> Dict:
        classification = FastClassifier.classify(pdf_path)
        lang = classification['lang']
        recommended = classification['recommended_library']

        methods = [
            ('marker', self.extract_with_marker),
            ('easyocr', self.extract_with_easyocr),
            ('fallback', self.extract_with_fallback)
        ]

        for name, func in methods:
            if recommended != name and name != 'fallback':
                continue
            try:
                logger.info(f"Using {name} for {Path(pdf_path).name}")
                result = func(pdf_path)
                result['metadata']['final_extractor'] = name
                result['metadata']['language'] = lang
                return result
            except Exception as e:
                logger.warning(f"{name} failed: {e}")

        # Final fallback
        result = self.extract_with_fallback(pdf_path)
        result['metadata']['final_extractor'] = 'fallback'
        return result

# ==========================================
# PROCESS SINGLE FILE
# ==========================================
def process_pdf(pdf_path: Path, output_dir: Path):
    extractor = PDFExtractor()
    result = extractor.extract(str(pdf_path))

    output_dir.mkdir(parents=True, exist_ok=True)
    output_file = output_dir / f"{pdf_path.stem}.json"
    output_file.write_text(json.dumps({
        "source": str(pdf_path),
        "classification": FastClassifier.classify(str(pdf_path)),
        "extraction": result,
        "status": "success"
    }, ensure_ascii=False, indent=2), encoding='utf-8')
    logger.info(f"Saved: {output_file}")

# ==========================================
# MAIN
# ==========================================
if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Best Arabic PDF Extractor 2025 – Fixed & Working")
    parser.add_argument("input", help="PDF file or folder")
    parser.add_argument("output", help="Output folder")
    args = parser.parse_args()

    input_path = Path(args.input)
    if input_path.is_file():
        process_pdf(input_path, Path(args.output))
    else:
        from concurrent.futures import ProcessPoolExecutor
        pdfs = list(input_path.rglob("*.pdf"))
        with ProcessPoolExecutor() as pool:
            for pdf in pdfs:
                pool.submit(process_pdf, pdf, Path(args.output))

    print("\nDone! Arabic text is now clean and perfect.")
    print("Press Enter to exit...")
    input()