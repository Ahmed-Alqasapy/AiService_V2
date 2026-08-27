"""
Enhanced Arabic PDF Extractor - December 2025
Structured output optimized for chunking and embedding
Preserves document hierarchy and semantics
"""

import os
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional
import fitz  # PyMuPDF
import re
from dataclasses import dataclass, asdict
from datetime import datetime

try:
    import regex as re
except ImportError:
    import re
    logging.warning("Install 'regex' for better Arabic handling: pip install regex")

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

# ==========================================
# LIBRARIES
# ==========================================
try:
    from marker.converters.pdf import PdfConverter
    from marker.models import create_model_dict
    MARKER_AVAILABLE = True
    logger.info("Marker loaded successfully")
except Exception as e:
    logger.error(f"Marker failed to load: {e}")
    MARKER_AVAILABLE = False

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
# DATA STRUCTURES FOR ORGANIZED OUTPUT
# ==========================================
@dataclass
class Section:
    """Represents a document section with hierarchy"""
    title: str
    content: str
    level: int  # 1=main, 2=sub, 3=sub-sub
    page_number: int
    section_type: str  # header, paragraph, table, list, etc.
    metadata: Dict = None
    
    def to_dict(self):
        return asdict(self)

@dataclass
class Table:
    """Represents extracted table data"""
    headers: List[str]
    rows: List[List[str]]
    caption: str
    page_number: int
    
    def to_dict(self):
        return asdict(self)

@dataclass
class DocumentStructure:
    """Complete document structure"""
    title: str
    source_file: str
    extraction_date: str
    language: str
    total_pages: int
    sections: List[Section]
    tables: List[Table]
    metadata: Dict
    
    def to_dict(self):
        return {
            'title': self.title,
            'source_file': self.source_file,
            'extraction_date': self.extraction_date,
            'language': self.language,
            'total_pages': self.total_pages,
            'sections': [s.to_dict() for s in self.sections],
            'tables': [t.to_dict() for t in self.tables],
            'metadata': self.metadata
        }

# ==========================================
# ENHANCED ARABIC TEXT PROCESSING
# ==========================================
class ArabicTextProcessor:
    """Advanced Arabic text cleaning and normalization"""
    
    @staticmethod
    def fix_arabic_word_spacing(text: str) -> str:
        """
        Fix Arabic words that are stuck together without spaces
        Uses intelligent pattern matching instead of huge dictionaries
        """
        if not text:
            return ""
        
        # 1. Add space between word + definite article (ال)
        # Pattern: [word][ال][word] → [word] [ال][word]
        text = re.sub(r'([\u0600-\u06FF]{3,})(ال[\u0600-\u06FF]{2,})', r'\1 \2', text)
        
        # 2. Add space before common prepositions
        prepositions = ['على', 'في', 'من', 'إلى', 'عن', 'لدى', 'مع', 'بين', 'ب']
        for prep in prepositions:
            # Word stuck to preposition: wordعلى → word على
            text = re.sub(f'([\\u0600-\\u06FF]{{3,}})({prep}\\b)', r'\1 \2', text)
        
        # 3. Add space before common conjunctions
        conjunctions = ['والتي', 'التي', 'الذي', 'التى', 'والذي', 'وال', 'أو']
        for conj in conjunctions:
            text = re.sub(f'([\\u0600-\\u06FF]{{3,}})({conj})', r'\1 \2', text)
        
        # 4. Add space before و (and) when it connects two words
        # Pattern: wordوword → word وword
        text = re.sub(r'([\u0600-\u06FF]{3,})(و[\u0600-\u06FF]{2,})', r'\1 \2', text)
        
        # 5. Add space after common question words
        question_words = ['ما', 'هل', 'متى', 'أين', 'كيف', 'لماذا']
        for q in question_words:
            text = re.sub(f'({q})([\\u0600-\\u06FF]{{3,}})', r'\1 \2', text)
        
        # 6. Fix common stuck patterns from your documents (minimal, high-impact only)
        critical_fixes = {
            'الدليلالارشادي': 'الدليل الارشادي',
            'حقالاطلاع': 'حق الاطلاع',
            'التجارةوالصناعة': 'التجارة والصناعة',
            'القانونرقم': 'القانون رقم',
            'لايمكن': 'لا يمكن',
            'لايترتب': 'لا يترتب',
            'لايزيد': 'لا يزيد',
            'مالم': 'ما لم',
            'بمالا': 'بما لا',
            'بمافي': 'بما في',
            'عنهالدى': 'عنها لدى',
        }
        
        for stuck, correct in critical_fixes.items():
            text = text.replace(stuck, correct)
        
        return text
    
    @staticmethod
    def fix_arabic_ocr_breaks(text: str) -> str:
        """Fix common Arabic OCR spacing issues"""
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
            r'\bم\s*ن\b': 'من',
            r'\bع\s*ن\b': 'عن',
            r'\bإ\s*لى\b': 'إلى',
        }
        
        for pattern, repl in fixes.items():
            text = re.sub(pattern, repl, text, flags=re.UNICODE)
        
        # Fix excessive spacing between Arabic characters (but not between words)
        text = re.sub(r'([\u0600-\u06FF])\s{2,}([\u0600-\u06FF])', r'\1 \2', text)
        return text
    
    @staticmethod
    def normalize_arabic(text: str) -> str:
        """Normalize Arabic characters"""
        # Normalize Alef variants
        text = re.sub('[إأآا]', 'ا', text)
        # Normalize Teh Marbuta
        text = re.sub('ة', 'ه', text)
        # Remove diacritics
        text = re.sub('[\u064B-\u065F]', '', text)
        return text
    
    @staticmethod
    def clean_text(text: str) -> str:
        """Comprehensive text cleaning"""
        if not text:
            return ""
        
        # STEP 1: Fix stuck Arabic words (most important for your case)
        text = ArabicTextProcessor.fix_arabic_word_spacing(text)
        
        # STEP 2: Remove watermarks and headers/footers patterns
        text = re.sub(r'[ىي]\s*م\s*ا\s*ي\s*م.*?ا\s*م\s*ر\s*م', '', text, flags=re.I)
        text = re.sub(r'أ\s*ي\s*م\s*ح\s*ر\s*ا.*?ي\s*م\s*م\s*ح', '', text)
        
        # STEP 3: Remove special symbols
        text = re.sub(r'◄|►|◆|▼|▲|★|▪|▫|•|→|←', '', text)
        
        # STEP 4: Fix OCR breaks
        text = ArabicTextProcessor.fix_arabic_ocr_breaks(text)
        
        # STEP 5: Normalize whitespace (but preserve paragraph breaks)
        text = re.sub(r'[ \t]+', ' ', text)  # Multiple spaces/tabs to single space
        text = re.sub(r'\n[ \t]+', '\n', text)  # Remove spaces at line start
        text = re.sub(r'[ \t]+\n', '\n', text)  # Remove spaces at line end
        text = re.sub(r'\n{3,}', '\n\n', text)  # Max 2 consecutive newlines
        
        return text.strip()
    
    @staticmethod
    def detect_section_level(text: str) -> int:
        """Detect heading level based on text characteristics"""
        # Check if it's a main title (shorter, contains keywords)
        main_keywords = ['الفهرس', 'المقدمة', 'الباب', 'الفصل', 'الوكالة', 'إدارة']
        if any(kw in text for kw in main_keywords) and len(text) < 100:
            return 1
        
        # Sub-section indicators
        if text.endswith(':') or text.endswith(':-'):
            return 2
        
        # Check if it's a numbered item
        if re.match(r'^\d+[\.\-)]', text):
            return 3
        
        return 2  # Default to sub-section

# ==========================================
# STRUCTURE EXTRACTOR
# ==========================================
class StructureExtractor:
    """Extract document structure and hierarchy"""
    
    @staticmethod
    def extract_tables_from_markdown(markdown_text: str, page_num: int = 0) -> List[Table]:
        """Extract tables from markdown format"""
        tables = []
        
        # Find markdown tables
        table_pattern = r'\|(.+)\|[\r\n]+\|[\s\-:]+\|[\r\n]+((?:\|.+\|[\r\n]+)+)'
        matches = re.finditer(table_pattern, markdown_text)
        
        for match in matches:
            header_line = match.group(1)
            body_lines = match.group(2)
            
            # Parse headers
            headers = [h.strip() for h in header_line.split('|') if h.strip()]
            
            # Parse rows
            rows = []
            for line in body_lines.strip().split('\n'):
                if '|' in line:
                    cells = [c.strip() for c in line.split('|') if c.strip()]
                    if cells:
                        rows.append(cells)
            
            if headers and rows:
                tables.append(Table(
                    headers=headers,
                    rows=rows,
                    caption="",
                    page_number=page_num
                ))
        
        return tables
    
    @staticmethod
    def extract_sections_from_text(text: str, language: str) -> List[Section]:
        """Extract hierarchical sections from text"""
        sections = []
        lines = text.split('\n')
        
        current_section = None
        current_content = []
        page_num = 0
        
        processor = ArabicTextProcessor()
        
        for i, line in enumerate(lines):
            line = line.strip()
            if not line:
                continue
            
            # Detect if this line is a header
            is_header = False
            section_level = 2
            
            # Check for various header patterns
            if language == 'arabic':
                # Main headers: short, contains keywords, ends with colon
                if len(line) < 100 and (
                    line.endswith(':') or 
                    line.endswith(':-') or
                    any(kw in line for kw in ['الباب', 'الفصل', 'الوكالة', 'إدارة', 'المادة'])
                ):
                    is_header = True
                    section_level = processor.detect_section_level(line)
            else:
                # English headers
                if line.isupper() or (len(line) < 80 and line.endswith(':')):
                    is_header = True
                    section_level = 1 if line.isupper() else 2
            
            if is_header:
                # Save previous section
                if current_section and current_content:
                    current_section.content = '\n'.join(current_content).strip()
                    sections.append(current_section)
                
                # Start new section
                current_section = Section(
                    title=processor.clean_text(line),
                    content="",
                    level=section_level,
                    page_number=page_num,
                    section_type='header',
                    metadata={}
                )
                current_content = []
            else:
                # Add to current section content
                if current_section:
                    current_content.append(line)
                else:
                    # No section yet, create a default one
                    current_section = Section(
                        title="مقدمة" if language == 'arabic' else "Introduction",
                        content="",
                        level=1,
                        page_number=0,
                        section_type='header',
                        metadata={}
                    )
                    current_content.append(line)
        
        # Save last section
        if current_section and current_content:
            current_section.content = '\n'.join(current_content).strip()
            sections.append(current_section)
        
        return sections
    
    @staticmethod
    def detect_list_items(text: str) -> List[str]:
        """Detect and extract list items"""
        # Arabic and English list patterns
        patterns = [
            r'^\d+[\.\-\)]',  # 1. or 1- or 1)
            r'^[أ-ي][\.\-\)]',  # أ. or أ- or أ)
            r'^[•\-\*]\s+',  # • or - or *
            r'^[\u0600-\u06FF]+[\.\-\)]',  # Arabic letter followed by . - )
        ]
        
        items = []
        for line in text.split('\n'):
            line = line.strip()
            if any(re.match(pattern, line) for pattern in patterns):
                items.append(line)
        
        return items

# ==========================================
# ENHANCED PDF CLASSIFIER
# ==========================================
class EnhancedClassifier:
    @staticmethod
    def classify(pdf_path: str) -> Dict:
        """Enhanced classification with more details"""
        try:
            doc = fitz.open(pdf_path)
            info = {
                'page_count': len(doc),
                'image_count': 0,
                'text_ratio': 0.0,
                'has_text': False,
                'is_scanned': False,
                'lang': 'unknown',
                'has_tables': False,
                'has_headers': False,
                'complexity': 'simple'
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
            
            full_text = ' '.join(sample_text)
            arabic_chars = len(re.findall(r'[\u0600-\u06FF]', full_text))
            total_chars = len(full_text.replace(' ', ''))
            info['lang'] = 'arabic' if arabic_chars / (total_chars or 1) > 0.3 else 'english'
            
            # Detect tables
            info['has_tables'] = '|' in full_text or 'جدول' in full_text or 'table' in full_text.lower()
            
            # Detect headers
            header_keywords = ['الباب', 'الفصل', 'Chapter', 'Section', 'المادة']
            info['has_headers'] = any(kw in full_text for kw in header_keywords)
            
            # Determine complexity
            if info['image_count'] > 10 or info['has_tables']:
                info['complexity'] = 'complex'
            elif info['has_headers']:
                info['complexity'] = 'medium'

            doc.close()

            # Recommend extractor
            if info['lang'] == 'arabic' or info['image_count'] > 10:
                recommended = 'marker'
            elif info['is_scanned']:
                recommended = 'easyocr' if EASYOCR_AVAILABLE else 'fallback'
            else:
                recommended = 'pymupdf4llm'

            info['recommended_library'] = recommended
            return info
            
        except Exception as e:
            logger.error(f"Classification failed: {e}")
            return {'recommended_library': 'marker', 'lang': 'arabic'}

# ==========================================
# ENHANCED PDF EXTRACTOR
# ==========================================
class EnhancedPDFExtractor:
    def __init__(self):
        self.marker_converter = None
        self.processor = ArabicTextProcessor()
        self.structure_extractor = StructureExtractor()
        
        if MARKER_AVAILABLE:
            try:
                logger.info("Initializing Marker with EasyOCR...")
                import easyocr
                _ = easyocr.Reader(['ar', 'en'], gpu=True, download_enabled=True)
                
                marker_config = {
                    "ocr": "easyocr",
                    "languages": ["ar", "en"],
                    "batch_size": 8,
                    "ocr_all_pages": True,
                    "ocr_image_dpi": 300,
                    "force_ocr": True,
                    "disable_tesseract": True,
                    "ocr_timeout": 300
                }

                self.marker_converter = PdfConverter(
                    artifact_dict=create_model_dict(),
                    config=marker_config
                )
                logger.info("Marker initialized successfully")
            except Exception as e:
                logger.error(f"Marker initialization failed: {e}")
                self.marker_converter = None

    def extract_with_marker(self, pdf_path: str, classification: Dict) -> DocumentStructure:
        """Extract with Marker and structure the output"""
        if not self.marker_converter:
            raise RuntimeError("Marker not ready")
        
        result = self.marker_converter(pdf_path)
        raw_markdown = result.markdown
        
        # Clean text
        cleaned_text = self.processor.clean_text(raw_markdown)
        
        # Extract structure
        sections = self.structure_extractor.extract_sections_from_text(
            cleaned_text, 
            classification['lang']
        )
        
        # Extract tables
        tables = self.structure_extractor.extract_tables_from_markdown(
            raw_markdown
        )
        
        # Detect document title (first significant line)
        title_match = re.search(r'^(.{10,100})$', cleaned_text, re.MULTILINE)
        title = title_match.group(1).strip() if title_match else Path(pdf_path).stem
        
        return DocumentStructure(
            title=title,
            source_file=str(pdf_path),
            extraction_date=datetime.now().isoformat(),
            language=classification['lang'],
            total_pages=classification['page_count'],
            sections=sections,
            tables=tables,
            metadata={
                'extractor': 'marker',
                'ocr_engine': 'easyocr_forced',
                'classification': classification
            }
        )

    def extract_with_fallback(self, pdf_path: str, classification: Dict) -> DocumentStructure:
        """Fallback extraction with structure"""
        if PYMUPDF4LLM_AVAILABLE:
            md = pymupdf4llm.to_markdown(pdf_path)
        else:
            doc = fitz.open(pdf_path)
            md = "\n\n".join(page.get_text("text") for page in doc)
            doc.close()
        
        cleaned_text = self.processor.clean_text(md)
        sections = self.structure_extractor.extract_sections_from_text(
            cleaned_text,
            classification['lang']
        )
        
        title_match = re.search(r'^(.{10,100})$', cleaned_text, re.MULTILINE)
        title = title_match.group(1).strip() if title_match else Path(pdf_path).stem
        
        return DocumentStructure(
            title=title,
            source_file=str(pdf_path),
            extraction_date=datetime.now().isoformat(),
            language=classification['lang'],
            total_pages=classification['page_count'],
            sections=sections,
            tables=[],
            metadata={
                'extractor': 'fallback',
                'classification': classification
            }
        )

    def extract(self, pdf_path: str) -> DocumentStructure:
        """Main extraction method with structured output"""
        classification = EnhancedClassifier.classify(pdf_path)
        recommended = classification['recommended_library']

        try:
            if recommended == 'marker' and self.marker_converter:
                logger.info(f"Using Marker for {Path(pdf_path).name}")
                return self.extract_with_marker(pdf_path, classification)
            else:
                logger.info(f"Using fallback for {Path(pdf_path).name}")
                return self.extract_with_fallback(pdf_path, classification)
        except Exception as e:
            logger.error(f"Extraction failed: {e}, using fallback")
            return self.extract_with_fallback(pdf_path, classification)

# ==========================================
# ENHANCED OUTPUT FORMATTING
# ==========================================
def save_structured_output(doc_structure: DocumentStructure, output_dir: Path):
    """Save in multiple formats for easy consumption"""
    output_dir.mkdir(parents=True, exist_ok=True)
    base_name = Path(doc_structure.source_file).stem
    
    # 1. JSON format (for programmatic access)
    json_file = output_dir / f"{base_name}_structured.json"
    with open(json_file, 'w', encoding='utf-8') as f:
        json.dump(doc_structure.to_dict(), f, ensure_ascii=False, indent=2)
    logger.info(f"Saved JSON: {json_file}")
    
    # 2. Markdown format (human-readable)
    md_file = output_dir / f"{base_name}_structured.md"
    with open(md_file, 'w', encoding='utf-8') as f:
        f.write(f"# {doc_structure.title}\n\n")
        f.write(f"**Source:** {doc_structure.source_file}\n")
        f.write(f"**Language:** {doc_structure.language}\n")
        f.write(f"**Pages:** {doc_structure.total_pages}\n")
        f.write(f"**Extracted:** {doc_structure.extraction_date}\n\n")
        f.write("---\n\n")
        
        for section in doc_structure.sections:
            heading_prefix = "#" * (section.level + 1)
            f.write(f"{heading_prefix} {section.title}\n\n")
            f.write(f"{section.content}\n\n")
        
        if doc_structure.tables:
            f.write("## Tables\n\n")
            for i, table in enumerate(doc_structure.tables, 1):
                f.write(f"### Table {i}\n\n")
                f.write("| " + " | ".join(table.headers) + " |\n")
                f.write("| " + " | ".join(["---"] * len(table.headers)) + " |\n")
                for row in table.rows:
                    f.write("| " + " | ".join(row) + " |\n")
                f.write("\n")
    
    logger.info(f"Saved Markdown: {md_file}")
    
    # 3. Plain text sections (for chunking)
    sections_dir = output_dir / f"{base_name}_sections"
    sections_dir.mkdir(exist_ok=True)
    
    for i, section in enumerate(doc_structure.sections):
        section_file = sections_dir / f"section_{i:03d}_{section.level}.txt"
        with open(section_file, 'w', encoding='utf-8') as f:
            f.write(f"TITLE: {section.title}\n")
            f.write(f"LEVEL: {section.level}\n")
            f.write(f"PAGE: {section.page_number}\n")
            f.write(f"---\n{section.content}")
    
    logger.info(f"Saved sections: {sections_dir}")

# ==========================================
# MAIN PROCESSING
# ==========================================
def process_pdf(pdf_path: Path, output_dir: Path):
    """Process single PDF with enhanced structure"""
    logger.info(f"Processing: {pdf_path}")
    
    extractor = EnhancedPDFExtractor()
    doc_structure = extractor.extract(str(pdf_path))
    
    save_structured_output(doc_structure, output_dir)
    
    logger.info(f"✓ Completed: {pdf_path.name}")
    logger.info(f"  - Sections: {len(doc_structure.sections)}")
    logger.info(f"  - Tables: {len(doc_structure.tables)}")

# ==========================================
# CLI
# ==========================================
if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(
        description="Enhanced Arabic PDF Extractor with Structured Output"
    )
    parser.add_argument("input", help="PDF file or folder")
    parser.add_argument("output", help="Output folder")
    parser.add_argument("--parallel", action="store_true", help="Process in parallel")
    
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    
    if input_path.is_file():
        process_pdf(input_path, output_path)
    else:
        pdfs = list(input_path.rglob("*.pdf"))
        logger.info(f"Found {len(pdfs)} PDF files")
        
        if args.parallel:
            from concurrent.futures import ProcessPoolExecutor
            with ProcessPoolExecutor() as pool:
                for pdf in pdfs:
                    pool.submit(process_pdf, pdf, output_path)
        else:
            for pdf in pdfs:
                process_pdf(pdf, output_path)

    print("\n✓ All done! Structured output ready for chunking and embedding.")
    print(f"Output location: {output_path}")