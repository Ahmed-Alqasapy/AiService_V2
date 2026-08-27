"""
Enhanced Arabic PDF Extractor for RAG - With Proper Table Extraction
Fixed December 2025 - RTL Table Support
"""
import os
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional, Tuple
import fitz  # PyMuPDF
import re
from dataclasses import dataclass, asdict
from datetime import datetime

try:
    import regex
    REGEX_AVAILABLE = True
except ImportError:
    import re as regex
    REGEX_AVAILABLE = False
    logging.warning("Install 'regex' for better Arabic: pip install regex")

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

# ==========================================
# LIBRARY DETECTION
# ==========================================
try:
    from marker.converters.pdf import PdfConverter
    from marker.models import create_model_dict
    MARKER_AVAILABLE = True
except Exception as e:
    logger.warning(f"Marker not available: {e}")
    MARKER_AVAILABLE = False

try:
    import easyocr
    EASYOCR_AVAILABLE = True
except Exception:
    EASYOCR_AVAILABLE = False

try:
    import pymupdf4llm
    PYMUPDF4LLM_AVAILABLE = True
except Exception:
    PYMUPDF4LLM_AVAILABLE = False

try:
    import camelot
    CAMELOT_AVAILABLE = True
    logger.info("✓ Camelot available for table extraction")
except Exception:
    CAMELOT_AVAILABLE = False
    logger.warning("Camelot not available. Install: pip install camelot-py[cv]")

try:
    import tabula
    TABULA_AVAILABLE = True
    logger.info("✓ Tabula available for table extraction")
except Exception:
    TABULA_AVAILABLE = False
    logger.warning("Tabula not available. Install: pip install tabula-py")

# ==========================================
# ENHANCED DATA STRUCTURES
# ==========================================
@dataclass
class Chunk:
    """Optimized chunk for RAG embedding"""
    chunk_id: str
    content: str
    metadata: Dict
    
    def to_dict(self):
        return asdict(self)

@dataclass
class Table:
    """Extracted table with metadata - RTL support"""
    id: str
    headers: List[str]
    rows: List[List[str]]
    caption: str
    section_id: str
    page_number: int
    rtl: bool = True  # Right-to-left flag for Arabic
    
    def to_dict(self):
        return asdict(self)
    
    def to_markdown(self) -> str:
        """Convert table to markdown format preserving RTL"""
        if not self.headers and not self.rows:
            return ""
        
        lines = []
        if self.caption:
            lines.append(f"**{self.caption}**\n")
        
        # Headers
        if self.headers:
            lines.append("| " + " | ".join(self.headers) + " |")
            lines.append("| " + " | ".join(["---"] * len(self.headers)) + " |")
        
        # Rows
        for row in self.rows:
            lines.append("| " + " | ".join(str(cell) for cell in row) + " |")
        
        return "\n".join(lines)
    
    def to_csv(self) -> str:
        """Convert table to CSV format"""
        lines = []
        if self.headers:
            lines.append(",".join(f'"{h}"' for h in self.headers))
        for row in self.rows:
            lines.append(",".join(f'"{cell}"' for cell in row))
        return "\n".join(lines)

@dataclass
class Section:
    """Document section with enhanced metadata"""
    id: str
    title: str
    content: str
    level: int
    page_start: int
    page_end: int
    parent_id: Optional[str] = None
    word_count: int = 0
    has_table: bool = False
    has_list: bool = False
    
    def to_dict(self):
        return asdict(self)

@dataclass
class DocumentOutput:
    """Clean document structure for RAG"""
    title: str
    language: str
    total_pages: int
    sections: List[Section]
    tables: List[Table]
    chunks: List[Chunk]
    full_text: str
    metadata: Dict
    
    def to_dict(self):
        return {
            'title': self.title,
            'language': self.language,
            'total_pages': self.total_pages,
            'sections': [s.to_dict() for s in self.sections],
            'tables': [t.to_dict() for t in self.tables],
            'chunks': [c.to_dict() for c in self.chunks],
            'metadata': self.metadata
        }

# ==========================================
# ENHANCED ARABIC TEXT PROCESSOR
# ==========================================
class ArabicTextProcessor:
    """Advanced Arabic text processing for RAG"""
    
    # Comprehensive stuck words dictionary
    STUCK_WORDS = {
        # From your document - CRITICAL FIXES
        'المعل ومات': 'المعلومات',
        'المعل ومات': 'المعلومات',
        'معل ومات': 'معلومات',
        'الق وانين': 'القوانين',
        'الق وانين': 'القوانين',
        'ق وانين': 'قوانين',
        'الل وائح': 'اللوائح',
        'ل وائح': 'لوائح',
        'الأ من': 'الأمن',
        'الأ من': 'الأمن',
        'أ من': 'أمن',
        'الأ وراق': 'الأوراق',
        'أ وراق': 'أوراق',
        'التح ويلات': 'التحويلات',
        'تح ويلات': 'تحويلات',
        'المصر فية': 'المصرفية',
        'مصر فية': 'مصرفية',
        'الم الية': 'المالية',
        'م الية': 'مالية',
        'مسئ ولياتها': 'مسئولياتها',
        'مسئ وليات': 'مسئوليات',
        'الاجت ماعية': 'الاجتماعية',
        'اجت ماعية': 'اجتماعية',
        'المست فيدين': 'المستفيدين',
        'مست فيدين': 'مستفيدين',
        'المقب ولين': 'المقبولين',
        'مقب ولين': 'مقبولين',
        'ال مناقصات': 'المناقصات',
        'مناقصات': 'مناقصات',
        
        # Common stuck patterns
        'الاطلاععلى': 'الاطلاع على',
        'لدىوزارة': 'لدى وزارة',
        'عنهالدى': 'عنها لدى',
        'الدليلالارشادي': 'الدليل الارشادي',
        'الدليلالإرشادي': 'الدليل الإرشادي',
        'وزارةالتجارة': 'وزارة التجارة',
        'مركزالكويت': 'مركز الكويت',
        'القانونرقم': 'القانون رقم',
        'المادةرقم': 'المادة رقم',
        'حقالاطلاع': 'حق الاطلاع',
        
        # Negations
        'لايمكن': 'لا يمكن',
        'لايجوز': 'لا يجوز',
        'لايترتب': 'لا يترتب',
        'لايزيد': 'لا يزيد',
        'لاتزيد': 'لا تزيد',
        'لاتقل': 'لا تقل',
        'لايقل': 'لا يقل',
        'لاتتجاوز': 'لا تتجاوز',
        'لايتجاوز': 'لا يتجاوز',
        
        # Common combinations
        'مالم': 'ما لم',
        'بمالا': 'بما لا',
        'بمافي': 'بما في',
        'ممافي': 'مما في',
        'فيما': 'في ما',
        'عماإذا': 'عما إذا',
        'إذاكان': 'إذا كان',
        'علىأن': 'على أن',
        'منشأن': 'من شأن',
        
        # Ministry/organization terms
        'التجارةوالصناعة': 'التجارة والصناعة',
        'الخدمةالمدنية': 'الخدمة المدنية',
        'مجلسالوزراء': 'مجلس الوزراء',
        
        # Common verbs
        'يجبتسجيل': 'يجب تسجيل',
        'يمكنكاضافة': 'يمكنك اضافة',
        'يجبعلى': 'يجب على',
    }

    
    @staticmethod
    def fix_broken_spaces(text: str) -> str:
        """Fix Arabic text with spaces breaking words"""
        patterns = [
            (r'م\s+ع\s+ل\s+و\s+م\s+ا\s+ت', 'معلومات'),
            (r'ق\s+و\s+ا\s+ن\s+ي\s+ن', 'قوانين'),
            (r'ل\s+و\s+ا\s+ئ\s+ح', 'لوائح'),
            (r'أ\s+م\s+ن', 'أمن'),
            (r'م\s+ص\s+ر\s+ف\s+ي\s+ة', 'مصرفية'),
        ]
        
        for pattern, replacement in patterns:
            text = regex.sub(pattern, replacement, text, flags=regex.IGNORECASE)
        
        return text
    
    @staticmethod
    def aggressive_word_separation(text: str) -> str:
        """Aggressively separate stuck Arabic words"""
        if not text:
            return ""
        
        for stuck, correct in ArabicTextProcessor.STUCK_WORDS.items():
            text = text.replace(stuck, correct)
        
        text = regex.sub(r'([\u0600-\u06FF]{3,})(ال[\u0600-\u06FF]{2,})', r'\1 \2', text)
        text = regex.sub(r'([\u0600-\u06FF]{3,})(و[\u0600-\u06FF]{3,})', r'\1 \2', text)
        text = regex.sub(r'\bلا([يتنأ][\u0600-\u06FF]{2,})', r'لا \1', text)
        
        return text
    
    @staticmethod
    def remove_markdown_symbols(text: str) -> str:
        """Remove markdown formatting symbols"""
        text = regex.sub(r'^#{1,6}\s+', '', text, flags=regex.MULTILINE)
        text = regex.sub(r'\*\*([^\*]+)\*\*', r'\1', text)
        text = regex.sub(r'\*([^\*]+)\*', r'\1', text)
        text = regex.sub(r'`([^`]+)`', r'\1', text)
        text = regex.sub(r'\[([^\]]+)\]\([^\)]+\)', r'\1', text)
        return text
    
    @staticmethod
    def remove_noise(text: str) -> str:
        """Remove image references and noise"""
        text = regex.sub(r'!\[\]\([^)]+\)', '', text)
        text = regex.sub(r'_page_\d+_Picture_\d+\.(jpeg|png|jpg)', '', text)
        text = regex.sub(r'^[◄►◆▼▲★▪▫→←]\s*', '- ', text, flags=regex.MULTILINE)
        text = text.replace('▶', '-')
        return text
    
    @staticmethod
    def normalize_whitespace(text: str) -> str:
        """Normalize whitespace while preserving structure"""
        text = regex.sub(r'[ \t]+', ' ', text)
        text = regex.sub(r'\n{3,}', '\n\n', text)
        return text.strip()
    
    @staticmethod
    def clean_text(text: str, remove_markdown: bool = True) -> str:
        """Complete cleaning pipeline"""
        if not text:
            return ""
        
        text = ArabicTextProcessor.fix_broken_spaces(text)
        for stuck, correct in ArabicTextProcessor.STUCK_WORDS.items():
            text = text.replace(stuck, correct)
        text = ArabicTextProcessor.remove_noise(text)
        if remove_markdown:
            text = ArabicTextProcessor.remove_markdown_symbols(text)
        text = ArabicTextProcessor.aggressive_word_separation(text)
        text = ArabicTextProcessor.normalize_whitespace(text)
        
        return text

# ==========================================
# ENHANCED TABLE EXTRACTOR
# ==========================================
class TableExtractor:
    """Extract tables from PDF preserving RTL structure"""
    
    def __init__(self):
        self.processor = ArabicTextProcessor()
    
    def extract_tables_pymupdf(self, pdf_path: str) -> List[Table]:
        """Extract tables using PyMuPDF with RTL awareness"""
        tables = []
        doc = fitz.open(pdf_path)
        
        for page_num, page in enumerate(doc, 1):
            # Find table structures
            table_blocks = self._find_table_blocks(page)
            
            for idx, table_data in enumerate(table_blocks, 1):
                table = Table(
                    id=f"table_p{page_num}_{idx}",
                    headers=table_data['headers'],
                    rows=table_data['rows'],
                    caption=table_data.get('caption', f"جدول {idx}"),
                    section_id="",
                    page_number=page_num,
                    rtl=True
                )
                tables.append(table)
        
        doc.close()
        logger.info(f"✓ Extracted {len(tables)} tables using PyMuPDF")
        return tables
    
    def _find_table_blocks(self, page) -> List[Dict]:
        """Find table structures in page"""
        tables = []
        
        # Get all text blocks with positions
        blocks = page.get_text("dict")["blocks"]
        
        # Find potential table regions (blocks with aligned structure)
        table_candidates = []
        for block in blocks:
            if "lines" in block:
                # Check if block has table-like structure
                if self._is_table_structure(block):
                    table_candidates.append(block)
        
        # Process each candidate
        for candidate in table_candidates:
            table_data = self._extract_table_from_block(candidate)
            if table_data:
                tables.append(table_data)
        
        return tables
    
    def _is_table_structure(self, block) -> bool:
        """Detect if block contains table structure"""
        lines = block.get("lines", [])
        if len(lines) < 2:
            return False
        
        # Check for consistent spacing/alignment
        x_positions = []
        for line in lines:
            for span in line.get("spans", []):
                x_positions.append(span["bbox"][0])
        
        # If we have consistent x positions, likely a table
        if len(set(round(x, -1) for x in x_positions)) >= 2:
            return True
        
        return False
    
    def _extract_table_from_block(self, block) -> Optional[Dict]:
        """Extract table data from block"""
        lines = block.get("lines", [])
        if not lines:
            return None
        
        rows = []
        for line in lines:
            cells = []
            for span in line.get("spans", []):
                text = span.get("text", "").strip()
                if text:
                    cells.append(self.processor.clean_text(text, remove_markdown=False))
            if cells:
                rows.append(cells)
        
        if len(rows) < 2:
            return None
        
        # First row as headers, rest as data
        return {
            'headers': rows[0] if rows else [],
            'rows': rows[1:] if len(rows) > 1 else [],
            'caption': ""
        }
    
    def extract_tables_camelot(self, pdf_path: str) -> List[Table]:
        """Extract tables using Camelot (more accurate)"""
        if not CAMELOT_AVAILABLE:
            return []
        
        tables = []
        try:
            # Extract tables from all pages
            camelot_tables = camelot.read_pdf(
                pdf_path,
                pages='all',
                flavor='lattice',  # Use 'stream' for borderless tables
                strip_text='\n'
            )
            
            for idx, ct in enumerate(camelot_tables, 1):
                df = ct.df
                page_num = ct.page
                
                # Clean and reverse for RTL
                headers = [self.processor.clean_text(str(h), remove_markdown=False) 
                          for h in df.iloc[0].tolist()]
                
                rows = []
                for _, row in df.iloc[1:].iterrows():
                    cleaned_row = [self.processor.clean_text(str(cell), remove_markdown=False) 
                                  for cell in row.tolist()]
                    rows.append(cleaned_row)
                
                table = Table(
                    id=f"table_p{page_num}_{idx}",
                    headers=headers,
                    rows=rows,
                    caption=f"جدول {idx} - صفحة {page_num}",
                    section_id="",
                    page_number=page_num,
                    rtl=True
                )
                tables.append(table)
            
            logger.info(f"✓ Extracted {len(tables)} tables using Camelot")
        except Exception as e:
            logger.warning(f"Camelot extraction failed: {e}")
        
        return tables
    
    def extract_tables_tabula(self, pdf_path: str) -> List[Table]:
        """Extract tables using Tabula"""
        if not TABULA_AVAILABLE:
            return []
        
        tables = []
        try:
            # Read all tables
            dfs = tabula.read_pdf(pdf_path, pages='all', multiple_tables=True)
            
            for idx, df in enumerate(dfs, 1):
                # Clean data
                headers = [self.processor.clean_text(str(h), remove_markdown=False) 
                          for h in df.columns.tolist()]
                
                rows = []
                for _, row in df.iterrows():
                    cleaned_row = [self.processor.clean_text(str(cell), remove_markdown=False) 
                                  for cell in row.tolist()]
                    rows.append(cleaned_row)
                
                table = Table(
                    id=f"table_{idx}",
                    headers=headers,
                    rows=rows,
                    caption=f"جدول {idx}",
                    section_id="",
                    page_number=0,
                    rtl=True
                )
                tables.append(table)
            
            logger.info(f"✓ Extracted {len(tables)} tables using Tabula")
        except Exception as e:
            logger.warning(f"Tabula extraction failed: {e}")
        
        return tables
    
    def extract_tables(self, pdf_path: str, method: str = 'auto') -> List[Table]:
        """Extract tables using best available method"""
        tables = []
        
        if method == 'auto':
            # Try methods in order of accuracy
            if CAMELOT_AVAILABLE:
                tables = self.extract_tables_camelot(pdf_path)
            
            if not tables and TABULA_AVAILABLE:
                tables = self.extract_tables_tabula(pdf_path)
            
            if not tables:
                tables = self.extract_tables_pymupdf(pdf_path)
        elif method == 'camelot' and CAMELOT_AVAILABLE:
            tables = self.extract_tables_camelot(pdf_path)
        elif method == 'tabula' and TABULA_AVAILABLE:
            tables = self.extract_tables_tabula(pdf_path)
        else:
            tables = self.extract_tables_pymupdf(pdf_path)
        
        return tables

# ==========================================
# SECTION DETECTOR
# ==========================================
class SectionDetector:
    """Improved section and heading detection"""
    
    HEADING_KEYWORDS = [
        'الباب', 'الفصل', 'المادة', 'القسم', 'الجزء', 'الفهرس',
        'المقدمة', 'الخاتمة', 'الملحق', 'التعريفات', 'المراجع',
        'الأهداف', 'النطاق', 'الإجراءات', 'الشروط', 'الأحكام',
        'أولا', 'ثانيا', 'ثالثا', 'رابعا', 'خامسا',
        'قوائم', 'قائمة'
    ]
    
    @staticmethod
    def is_heading(line: str) -> Tuple[bool, int]:
        """Detect if line is a heading and its level"""
        line = line.strip()
        clean_line = line.lstrip('•،- ').strip()
        
        if not clean_line or len(clean_line) > 200:
            return False, 0
        
        if line.startswith('•') and 'قوائم' in line:
            return True, 2
        
        if any(clean_line.startswith(kw) for kw in ['أولا', 'ثانيا', 'ثالثا']):
            return True, 1
        
        if any(kw in clean_line for kw in SectionDetector.HEADING_KEYWORDS) and len(clean_line) < 120:
            return True, 2
        
        return False, 0
    
    @staticmethod
    def extract_sections(text: str, language: str = 'arabic') -> List[Section]:
        """Extract hierarchical sections"""
        lines = text.split('\n')
        sections = []
        
        current_section = None
        current_content = []
        section_counter = 0
        
        for line in lines:
            line = line.strip()
            if not line:
                continue
            
            is_heading, level = SectionDetector.is_heading(line)
            
            if is_heading:
                if current_section and current_content:
                    content_text = '\n'.join(current_content).strip()
                    current_section.content = content_text
                    current_section.word_count = len(content_text.split())
                    sections.append(current_section)
                
                section_counter += 1
                clean_title = line.lstrip('•،- ').strip()
                current_section = Section(
                    id=f"section_{section_counter:03d}",
                    title=clean_title,
                    content="",
                    level=level,
                    page_start=0,
                    page_end=0,
                    word_count=0
                )
                current_content = []
            else:
                if current_section is None:
                    section_counter += 1
                    current_section = Section(
                        id=f"section_{section_counter:03d}",
                        title="المحتوى" if language == 'arabic' else "Content",
                        content="",
                        level=1,
                        page_start=0,
                        page_end=0,
                        word_count=0
                    )
                
                current_content.append(line)
        
        if current_section and current_content:
            content_text = '\n'.join(current_content).strip()
            current_section.content = content_text
            current_section.word_count = len(content_text.split())
            sections.append(current_section)
        
        return sections

# ==========================================
# SMART CHUNKER
# ==========================================
class SmartChunker:
    """Intelligent chunking for RAG embeddings"""
    
    @staticmethod
    def chunk_by_sections(sections: List[Section], 
                          max_chunk_size: int = 1000,
                          overlap: int = 100) -> List[Chunk]:
        """Create chunks optimized for embedding"""
        chunks = []
        chunk_counter = 0
        
        for section in sections:
            content = section.content
            words = content.split()
            
            if len(words) <= max_chunk_size:
                chunk_counter += 1
                chunks.append(Chunk(
                    chunk_id=f"chunk_{chunk_counter:04d}",
                    content=f"{section.title}\n\n{content}",
                    metadata={
                        'section_id': section.id,
                        'section_title': section.title,
                        'level': section.level,
                        'word_count': len(words),
                        'type': 'full_section'
                    }
                ))
            else:
                start = 0
                part = 1
                
                while start < len(words):
                    end = start + max_chunk_size
                    chunk_words = words[start:end]
                    
                    chunk_counter += 1
                    chunk_text = ' '.join(chunk_words)
                    
                    if part == 1:
                        chunk_text = f"{section.title}\n\n{chunk_text}"
                    
                    chunks.append(Chunk(
                        chunk_id=f"chunk_{chunk_counter:04d}",
                        content=chunk_text,
                        metadata={
                            'section_id': section.id,
                            'section_title': section.title,
                            'level': section.level,
                            'word_count': len(chunk_words),
                            'type': 'partial_section',
                            'part': part
                        }
                    ))
                    
                    start = end - overlap
                    part += 1
        
        return chunks

# ==========================================
# ENHANCED PDF EXTRACTOR WITH TABLES
# ==========================================
class EnhancedRAGExtractor:
    """Main extractor optimized for RAG with table support"""
   
    def __init__(self, remove_markdown: bool = True):
        self.marker_converter = None
        self.processor = ArabicTextProcessor()
        self.detector = SectionDetector()
        self.chunker = SmartChunker()
        self.table_extractor = TableExtractor()
        self.remove_markdown = remove_markdown
       
        if MARKER_AVAILABLE:
            try:
                marker_config = {
                    "ocr": "easyocr",
                    "languages": ["ar", "en"],
                    "batch_size": 8,
                    "ocr_all_pages": True,
                    "force_ocr": True
                }
                self.marker_converter = PdfConverter(
                    artifact_dict=create_model_dict(),
                    config=marker_config
                )
                logger.info("✓ Marker initialized")
            except Exception as e:
                logger.warning(f"Marker init failed: {e}")
                self.marker_converter = None

    def extract(self, pdf_path: str, max_chunk_size: int = 1000, 
                extract_tables: bool = True, table_method: str = 'auto') -> DocumentOutput:
        """Extract and structure PDF for RAG with tables"""
        logger.info(f"Processing: {Path(pdf_path).name}")
       
        # Get raw text
        if self.marker_converter:
            result = self.marker_converter(pdf_path)
            raw_text = result.markdown
            method = "marker"
        elif PYMUPDF4LLM_AVAILABLE:
            raw_text = pymupdf4llm.to_markdown(pdf_path)
            method = "pymupdf4llm"
        else:
            doc = fitz.open(pdf_path)
            raw_text = "\n\n".join(page.get_text() for page in doc)
            doc.close()
            method = "pymupdf"
       
        logger.info(f"✓ Extracted text using {method}")
        
        # Extract tables
        tables = []
        if extract_tables:
            logger.info("📊 Extracting tables...")
            tables = self.table_extractor.extract_tables(pdf_path, method=table_method)
            logger.info(f"✓ Extracted {len(tables)} tables")
        
        # Clean text
        logger.info("🧹 Cleaning Arabic text...")
        cleaned_text = self.processor.clean_text(raw_text, remove_markdown=self.remove_markdown)
        logger.info(f"✓ Text cleaned (reduced from {len(raw_text)} to {len(cleaned_text)} chars)")
        
        # Detect language
        arabic_ratio = len(regex.findall(r'[\u0600-\u06FF]', cleaned_text)) / max(len(cleaned_text), 1)
        language = 'arabic' if arabic_ratio > 0.3 else 'english'
        logger.info(f"✓ Detected language: {language} ({arabic_ratio:.1%} Arabic)")
       
        # Extract sections
        logger.info("📑 Extracting sections...")
        sections = self.detector.extract_sections(cleaned_text, language)
        logger.info(f"✓ Found {len(sections)} sections")
       
        # Detect lists
        for section in sections:
            if any(line.strip().startswith(('- ', '• ', '* ', '1.', '2.')) 
                   for line in section.content.split('\n')):
                section.has_list = True
        
        # Mark sections with tables
        for table in tables:
            for section in sections:
                if section.page_start <= table.page_number <= section.page_end:
                    section.has_table = True
                    table.section_id = section.id
       
        # Create chunks
        logger.info("✂️ Creating chunks...")
        chunks = self.chunker.chunk_by_sections(sections, max_chunk_size)
        logger.info(f"✓ Created {len(chunks)} chunks")
       
        # Detect title
        title_match = regex.search(r'^([^\n]{10,100})', cleaned_text, regex.MULTILINE)
        title = title_match.group(1).strip() if title_match else Path(pdf_path).stem
       
        # Page count
        doc = fitz.open(pdf_path)
        page_count = len(doc)
        doc.close()
       
        return DocumentOutput(
            title=title,
            language=language,
            total_pages=page_count,
            sections=sections,
            tables=tables,
            chunks=chunks,
            full_text=cleaned_text,
            metadata={
                'source_file': str(pdf_path),
                'extraction_method': method,
                'table_extraction_method': table_method if extract_tables else 'none',
                'extraction_date': datetime.now().isoformat(),
                'total_sections': len(sections),
                'total_chunks': len(chunks),
                'total_tables': len(tables),
            }
        )


# ==========================================
# OUTPUT FORMATTERS
# ==========================================
def save_for_rag(doc_output: DocumentOutput, output_dir: Path):
    """Save in RAG-optimized formats including tables"""
    output_dir.mkdir(parents=True, exist_ok=True)
    base_name = Path(doc_output.metadata['source_file']).stem

    # 1. Chunks JSON
    chunks_file = output_dir / f"{base_name}_chunks.json"
    with open(chunks_file, 'w', encoding='utf-8') as f:
        json.dump([c.to_dict() for c in doc_output.chunks], f, ensure_ascii=False, indent=2)
    logger.info(f"✓ Chunks JSON saved: {chunks_file}")

    # 2. Clean full text
    txt_file = output_dir / f"{base_name}_clean.txt"
    with open(txt_file, 'w', encoding='utf-8') as f:
        f.write(doc_output.full_text)
    logger.info(f"✓ Clean text saved: {txt_file}")

    # 3. Sections JSON
    sections_file = output_dir / f"{base_name}_sections.json"
    with open(sections_file, 'w', encoding='utf-8') as f:
        json.dump([s.to_dict() for s in doc_output.sections], f, ensure_ascii=False, indent=2)
    logger.info(f"✓ Sections JSON saved: {sections_file}")

    # 4. Tables - Multiple formats
    if doc_output.tables:
        # Tables JSON
        tables_file = output_dir / f"{base_name}_tables.json"
        with open(tables_file, 'w', encoding='utf-8') as f:
            json.dump([t.to_dict() for t in doc_output.tables], f, ensure_ascii=False, indent=2)
        logger.info(f"✓ Tables JSON saved: {tables_file}")
        
        # Tables Markdown (human-readable)
        tables_md_file = output_dir / f"{base_name}_tables.md"
        with open(tables_md_file, 'w', encoding='utf-8') as f:
            for table in doc_output.tables:
                f.write(f"\n## {table.caption}\n\n")
                f.write(table.to_markdown())
                f.write("\n\n---\n")
        logger.info(f"✓ Tables Markdown saved: {tables_md_file}")
        
        # Individual CSV files for each table
        tables_csv_dir = output_dir / f"{base_name}_tables_csv"
        tables_csv_dir.mkdir(exist_ok=True)
        for table in doc_output.tables:
            csv_file = tables_csv_dir / f"{table.id}.csv"
            with open(csv_file, 'w', encoding='utf-8') as f:
                f.write(table.to_csv())
        logger.info(f"✓ Tables CSV saved: {tables_csv_dir}")

    # 5. Full structured output
    full_file = output_dir / f"{base_name}_full.json"
    with open(full_file, 'w', encoding='utf-8') as f:
        json.dump(doc_output.to_dict(), f, ensure_ascii=False, indent=2)
    logger.info(f"✓ Full structured output saved: {full_file}")


# ==========================================
# MAIN EXECUTION
# ==========================================
def process_pdf(pdf_path: Path, output_dir: Path, chunk_size: int = 1000, 
                extract_tables: bool = True, table_method: str = 'auto'):
    extractor = EnhancedRAGExtractor()
    doc_output = extractor.extract(str(pdf_path), chunk_size, extract_tables, table_method)
    save_for_rag(doc_output, output_dir)
    logger.info(f"✅ Completed: {pdf_path.name} → {len(doc_output.chunks)} chunks, {len(doc_output.tables)} tables")

if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Enhanced Arabic PDF Extractor for RAG with Table Support")
    parser.add_argument("input", help="Input PDF file or directory")
    parser.add_argument("output", help="Output directory")
    parser.add_argument("--chunk-size", type=int, default=1000, help="Max words per chunk")
    parser.add_argument("--no-tables", action="store_true", help="Skip table extraction")
    parser.add_argument("--table-method", choices=['auto', 'camelot', 'tabula', 'pymupdf'], 
                        default='auto', help="Table extraction method")
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    output_path.mkdir(parents=True, exist_ok=True)

    extract_tables = not args.no_tables

    if input_path.is_file():
        process_pdf(input_path, output_path, args.chunk_size, extract_tables, args.table_method)
    else:
        pdfs = list(input_path.rglob("*.pdf"))
        logger.info(f"Found {len(pdfs)} PDFs")
        for pdf in pdfs:
            try:
                process_pdf(pdf, output_path, args.chunk_size, extract_tables, args.table_method)
            except Exception as e:
                logger.error(f"Failed to process {pdf.name}: {e}")

    print(f"\n✅ All done! Output saved to: {output_path}")
    if extract_tables:
        print("📊 Tables extracted in JSON, Markdown, and CSV formats")