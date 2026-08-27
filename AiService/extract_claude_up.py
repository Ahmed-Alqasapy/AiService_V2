"""
Part 1: Core Data Structures and Text Processing
Enhanced for Large PDF Handling - December 2025
"""
import os
import json
import logging
from dataclasses import dataclass, asdict
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Tuple
import fitz  # PyMuPDF
from typing import Optional

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
    
    def get_token_estimate(self) -> int:
        """Estimate tokens (rough approximation)"""
        return len(self.content.split()) * 1.3  # Arabic tokens are typically 1.3x words

@dataclass
class Table:
    """Extracted table with metadata - RTL support"""
    id: str
    headers: List[str]
    rows: List[List[str]]
    caption: str
    section_id: str
    page_number: int
    rtl: bool = True
    
    def to_dict(self):
        return asdict(self)
    
    def to_markdown(self) -> str:
        """Convert table to markdown format preserving RTL"""
        if not self.headers and not self.rows:
            return ""
        
        lines = []
        if self.caption:
            lines.append(f"**{self.caption}**\n")
        
        if self.headers:
            lines.append("| " + " | ".join(self.headers) + " |")
            lines.append("| " + " | ".join(["---"] * len(self.headers)) + " |")
        
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
    char_count: int = 0
    
    def to_dict(self):
        return asdict(self)
    
    def __post_init__(self):
        """Calculate metadata after initialization"""
        if self.word_count == 0:
            self.word_count = len(self.content.split())
        if self.char_count == 0:
            self.char_count = len(self.content)

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
    
    def get_summary_stats(self) -> Dict:
        """Get document statistics"""
        return {
            'total_pages': self.total_pages,
            'total_sections': len(self.sections),
            'total_tables': len(self.tables),
            'total_chunks': len(self.chunks),
            'total_words': sum(s.word_count for s in self.sections),
            'total_chars': len(self.full_text),
            'avg_section_length': sum(s.word_count for s in self.sections) / max(len(self.sections), 1),
            'avg_chunk_length': sum(len(c.content.split()) for c in self.chunks) / max(len(self.chunks), 1)
        }

# ==========================================
# ENHANCED ARABIC TEXT PROCESSOR
# ==========================================
class ArabicTextProcessor:
    """Advanced Arabic text processing for RAG - Memory efficient"""
    
    STUCK_WORDS = {
        'المعل ومات': 'المعلومات', 'معل ومات': 'معلومات',
        'الق وانين': 'القوانين', 'ق وانين': 'قوانين',
        'الل وائح': 'اللوائح', 'ل وائح': 'لوائح',
        'الأ من': 'الأمن', 'أ من': 'أمن',
        'الأ وراق': 'الأوراق', 'أ وراق': 'أوراق',
        'التح ويلات': 'التحويلات', 'تح ويلات': 'تحويلات',
        'المصر فية': 'المصرفية', 'مصر فية': 'مصرفية',
        'الم الية': 'المالية', 'م الية': 'مالية',
        'مسئ ولياتها': 'مسئولياتها', 'مسئ وليات': 'مسئوليات',
        'الاجت ماعية': 'الاجتماعية', 'اجت ماعية': 'اجتماعية',
        'المست فيدين': 'المستفيدين', 'مست فيدين': 'مستفيدين',
        'المقب ولين': 'المقبولين', 'مقب ولين': 'مقبولين',
        'الاطلاععلى': 'الاطلاع على', 'لدىوزارة': 'لدى وزارة',
        'عنهالدى': 'عنها لدى', 'الدليلالارشادي': 'الدليل الارشادي',
        'وزارةالتجارة': 'وزارة التجارة', 'مركزالكويت': 'مركز الكويت',
        'القانونرقم': 'القانون رقم', 'المادةرقم': 'المادة رقم',
        'لايمكن': 'لا يمكن', 'لايجوز': 'لا يجوز',
        'لايترتب': 'لا يترتب', 'لايزيد': 'لا يزيد',
        'مالم': 'ما لم', 'بمالا': 'بما لا', 'فيما': 'في ما',
        'إذاكان': 'إذا كان', 'علىأن': 'على أن',
        'التجارةوالصناعة': 'التجارة والصناعة',
        'الخدمةالمدنية': 'الخدمة المدنية',
        'مجلسالوزراء': 'مجلس الوزراء',
        'يجبتسجيل': 'يجب تسجيل', 'يمكنكاضافة': 'يمكنك اضافة',
    }
    
    # Compile regex patterns once for performance
    BROKEN_SPACE_PATTERNS = [
        (regex.compile(r'م\s+ع\s+ل\s+و\s+م\s+ا\s+ت', regex.IGNORECASE), 'معلومات'),
        (regex.compile(r'ق\s+و\s+ا\s+ن\s+ي\s+ن', regex.IGNORECASE), 'قوانين'),
        (regex.compile(r'ل\s+و\s+ا\s+ئ\s+ح', regex.IGNORECASE), 'لوائح'),
        (regex.compile(r'أ\s+م\s+ن', regex.IGNORECASE), 'أمن'),
        (regex.compile(r'م\s+ص\s+ر\s+ف\s+ي\s+ة', regex.IGNORECASE), 'مصرفية'),
    ]
    
    WORD_SEPARATION_PATTERNS = [
        (regex.compile(r'([\u0600-\u06FF]{3,})(ال[\u0600-\u06FF]{2,})'), r'\1 \2'),
        (regex.compile(r'([\u0600-\u06FF]{3,})(و[\u0600-\u06FF]{3,})'), r'\1 \2'),
        (regex.compile(r'\bلا([يتنأ][\u0600-\u06FF]{2,})'), r'لا \1'),
    ]
    
    @staticmethod
    def fix_broken_spaces(text: str) -> str:
        """Fix Arabic text with spaces breaking words - optimized"""
        if not text:
            return ""
        
        for pattern, replacement in ArabicTextProcessor.BROKEN_SPACE_PATTERNS:
            text = pattern.sub(replacement, text)
        
        return text
    
    @staticmethod
    def aggressive_word_separation(text: str) -> str:
        """Aggressively separate stuck Arabic words - optimized"""
        if not text:
            return ""
        
        # Apply stuck words dictionary (most common fixes)
        for stuck, correct in ArabicTextProcessor.STUCK_WORDS.items():
            if stuck in text:
                text = text.replace(stuck, correct)
        
        # Apply regex patterns for dynamic separation
        for pattern, replacement in ArabicTextProcessor.WORD_SEPARATION_PATTERNS:
            text = pattern.sub(replacement, text)
        
        return text
    
    @staticmethod
    def remove_markdown_symbols(text: str) -> str:
        """Remove markdown formatting symbols"""
        if not text:
            return ""
        
        text = regex.sub(r'^#{1,6}\s+', '', text, flags=regex.MULTILINE)
        text = regex.sub(r'\*\*([^\*]+)\*\*', r'\1', text)
        text = regex.sub(r'\*([^\*]+)\*', r'\1', text)
        text = regex.sub(r'`([^`]+)`', r'\1', text)
        text = regex.sub(r'\[([^\]]+)\]\([^\)]+\)', r'\1', text)
        return text
    
    @staticmethod
    def remove_noise(text: str) -> str:
        """Remove image references and noise"""
        if not text:
            return ""
        
        text = regex.sub(r'!\[\]\([^)]+\)', '', text)
        text = regex.sub(r'_page_\d+_Picture_\d+\.(jpeg|png|jpg)', '', text)
        text = regex.sub(r'^[◄►◆▼▲★▪▫→←]\s*', '- ', text, flags=regex.MULTILINE)
        text = text.replace('▶', '-')
        return text
    
    @staticmethod
    def normalize_whitespace(text: str) -> str:
        """Normalize whitespace while preserving structure"""
        if not text:
            return ""
        
        text = regex.sub(r'[ \t]+', ' ', text)
        text = regex.sub(r'\n{3,}', '\n\n', text)
        return text.strip()
    
    @staticmethod
    def clean_text(text: str, remove_markdown: bool = True) -> str:
        """Complete cleaning pipeline - memory efficient"""
        if not text:
            return ""
        
        # Process in order of importance
        text = ArabicTextProcessor.fix_broken_spaces(text)
        
        # Batch replace stuck words (more efficient than individual replaces)
        for stuck, correct in ArabicTextProcessor.STUCK_WORDS.items():
            if stuck in text:  # Check before replace for efficiency
                text = text.replace(stuck, correct)
        
        text = ArabicTextProcessor.remove_noise(text)
        
        if remove_markdown:
            text = ArabicTextProcessor.remove_markdown_symbols(text)
        
        text = ArabicTextProcessor.aggressive_word_separation(text)
        text = ArabicTextProcessor.normalize_whitespace(text)
        
        return text
    
    @staticmethod
    def clean_text_streaming(text: str, chunk_size: int = 50000, 
                            remove_markdown: bool = True) -> str:
        """Clean text in chunks for very large documents"""
        if not text or len(text) < chunk_size:
            return ArabicTextProcessor.clean_text(text, remove_markdown)
        
        cleaned_chunks = []
        for i in range(0, len(text), chunk_size):
            chunk = text[i:i + chunk_size]
            cleaned_chunk = ArabicTextProcessor.clean_text(chunk, remove_markdown)
            cleaned_chunks.append(cleaned_chunk)
        
        return "\n".join(cleaned_chunks)

# ==========================================
# SECTION DETECTOR
# ==========================================
class SectionDetector:
    """Improved section and heading detection - optimized for large documents"""
    
    HEADING_KEYWORDS = [
        'الباب', 'الفصل', 'المادة', 'القسم', 'الجزء', 'الفهرس',
        'المقدمة', 'الخاتمة', 'الملحق', 'التعريفات', 'المراجع',
        'الأهداف', 'النطاق', 'الإجراءات', 'الشروط', 'الأحكام',
        'أولا', 'ثانيا', 'ثالثا', 'رابعا', 'خامسا',
        'قوائم', 'قائمة'
    ]
    
    @staticmethod
    def is_heading(line: str) -> tuple[bool, int]:
        """Detect if line is a heading and its level"""
        if not line:
            return False, 0
        
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
        """Extract hierarchical sections - memory efficient"""
        if not text:
            return []
        
        lines = text.split('\n')
        sections = []
        
        current_section = None
        current_content = []
        section_counter = 0
        
        for line_num, line in enumerate(lines):
            line = line.strip()
            if not line:
                continue
            
            is_heading, level = SectionDetector.is_heading(line)
            
            if is_heading:
                # Save previous section
                if current_section and current_content:
                    content_text = '\n'.join(current_content).strip()
                    current_section.content = content_text
                    current_section.word_count = len(content_text.split())
                    current_section.char_count = len(content_text)
                    sections.append(current_section)
                
                # Start new section
                section_counter += 1
                clean_title = line.lstrip('•،- ').strip()
                current_section = Section(
                    id=f"section_{section_counter:03d}",
                    title=clean_title,
                    content="",
                    level=level,
                    page_start=0,
                    page_end=0,
                    word_count=0,
                    char_count=0
                )
                current_content = []
            else:
                # Create default section if none exists
                if current_section is None:
                    section_counter += 1
                    current_section = Section(
                        id=f"section_{section_counter:03d}",
                        title="المحتوى" if language == 'arabic' else "Content",
                        content="",
                        level=1,
                        page_start=0,
                        page_end=0,
                        word_count=0,
                        char_count=0
                    )
                
                current_content.append(line)
        
        # Save final section
        if current_section and current_content:
            content_text = '\n'.join(current_content).strip()
            current_section.content = content_text
            current_section.word_count = len(content_text.split())
            current_section.char_count = len(content_text)
            sections.append(current_section)
        
        logger.info(f"✓ Extracted {len(sections)} sections from {len(lines)} lines")
        return sections

# ==========================================
# SMART CHUNKER
# ==========================================
class SmartChunker:
    """Intelligent chunking for RAG embeddings - handles large sections"""
    
    @staticmethod
    def chunk_by_sections(sections: List[Section], 
                          max_chunk_size: int = 1000,
                          overlap: int = 100) -> List[Chunk]:
        """Create chunks optimized for embedding - memory efficient"""
        chunks = []
        chunk_counter = 0
        
        for section in sections:
            content = section.content
            if not content:
                continue
            
            words = content.split()
            
            if len(words) <= max_chunk_size:
                # Small section - single chunk
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
                # Large section - split into multiple chunks
                start = 0
                part = 1
                
                while start < len(words):
                    end = start + max_chunk_size
                    chunk_words = words[start:end]
                    
                    chunk_counter += 1
                    chunk_text = ' '.join(chunk_words)
                    
                    # Add section title to first part only
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
                            'part': part,
                            'total_parts': (len(words) + max_chunk_size - overlap - 1) // (max_chunk_size - overlap)
                        }
                    ))
                    
                    start = end - overlap
                    part += 1
        
        logger.info(f"✓ Created {len(chunks)} chunks from {len(sections)} sections")
        return chunks


# ==========================================
# LIBRARY DETECTION
# ==========================================
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

try:
    from marker.converters.pdf import PdfConverter
    from marker.models import create_model_dict
    MARKER_AVAILABLE = True
    logger.info("✓ Marker available for advanced extraction")
except Exception as e:
    MARKER_AVAILABLE = False
    logger.warning(f"Marker not available: {e}")

try:
    import pymupdf4llm
    PYMUPDF4LLM_AVAILABLE = True
    logger.info("✓ PyMuPDF4LLM available")
except Exception:
    PYMUPDF4LLM_AVAILABLE = False
    logger.warning("PyMuPDF4LLM not available")

# ==========================================
# ENHANCED TABLE EXTRACTOR
# ==========================================
class TableExtractor:
    """Extract tables from PDF preserving RTL structure - handles large PDFs"""
    
    def __init__(self):
        self.processor = ArabicTextProcessor()
    
    def extract_tables_pymupdf(self, pdf_path: str, page_range: Optional[tuple[int, int]] = None) -> List[Table]:
        """Extract tables using PyMuPDF with RTL awareness - page-by-page processing"""
        tables = []
        doc = fitz.open(pdf_path)
        
        # Determine page range
        start_page = page_range[0] if page_range else 0
        end_page = page_range[1] if page_range else len(doc)
        
        logger.info(f"Extracting tables from pages {start_page+1} to {end_page}")
        
        for page_num in range(start_page, end_page):
            try:
                page = doc[page_num]
                table_blocks = self._find_table_blocks(page)
                
                for idx, table_data in enumerate(table_blocks, 1):
                    table = Table(
                        id=f"table_p{page_num+1}_{idx}",
                        headers=table_data['headers'],
                        rows=table_data['rows'],
                        caption=table_data.get('caption', f"جدول {idx}"),
                        section_id="",
                        page_number=page_num + 1,
                        rtl=True
                    )
                    tables.append(table)
            except Exception as e:
                logger.warning(f"Error extracting tables from page {page_num+1}: {e}")
                continue
        
        doc.close()
        logger.info(f"✓ Extracted {len(tables)} tables using PyMuPDF")
        return tables
    
    def _find_table_blocks(self, page) -> List[Dict]:
        """Find table structures in page"""
        tables = []
        
        try:
            blocks = page.get_text("dict")["blocks"]
            
            table_candidates = []
            for block in blocks:
                if "lines" in block and self._is_table_structure(block):
                    table_candidates.append(block)
            
            for candidate in table_candidates:
                table_data = self._extract_table_from_block(candidate)
                if table_data:
                    tables.append(table_data)
        except Exception as e:
            logger.debug(f"Error finding table blocks: {e}")
        
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
        if len(x_positions) >= 4:
            unique_positions = len(set(round(x, -1) for x in x_positions))
            if unique_positions >= 2:
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
        
        return {
            'headers': rows[0] if rows else [],
            'rows': rows[1:] if len(rows) > 1 else [],
            'caption': ""
        }
    
    def extract_tables_camelot(self, pdf_path: str, page_range: Optional[tuple[int, int]] = None) -> List[Table]:
        """Extract tables using Camelot - batch processing for large PDFs"""
        if not CAMELOT_AVAILABLE:
            logger.warning("Camelot not available, skipping")
            return []
        
        tables = []
        try:
            # Determine pages
            pages = 'all'
            if page_range:
                start, end = page_range
                pages = f"{start+1}-{end}"
            
            logger.info(f"Extracting tables with Camelot from pages: {pages}")
            
            # Extract tables
            camelot_tables = camelot.read_pdf(
                pdf_path,
                pages=pages,
                flavor='lattice',
                strip_text='\n',
                edge_tol=50,  # More forgiving for complex layouts
                row_tol=10
            )
            
            for idx, ct in enumerate(camelot_tables, 1):
                try:
                    df = ct.df
                    page_num = ct.page
                    
                    # Clean and process
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
                except Exception as e:
                    logger.warning(f"Error processing Camelot table {idx}: {e}")
                    continue
            
            logger.info(f"✓ Extracted {len(tables)} tables using Camelot")
        except Exception as e:
            logger.warning(f"Camelot extraction failed: {e}")
        
        return tables
    
    def extract_tables_tabula(self, pdf_path: str, page_range: Optional[tuple[int, int]] = None) -> List[Table]:
        """Extract tables using Tabula - batch processing"""
        if not TABULA_AVAILABLE:
            logger.warning("Tabula not available, skipping")
            return []
        
        tables = []
        try:
            # Determine pages
            pages = 'all'
            if page_range:
                start, end = page_range
                pages = list(range(start + 1, end + 1))
            
            logger.info(f"Extracting tables with Tabula from pages: {pages}")
            
            dfs = tabula.read_pdf(
                pdf_path, 
                pages=pages, 
                multiple_tables=True,
                stream=True  # Better for Arabic text
            )
            
            for idx, df in enumerate(dfs, 1):
                try:
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
                except Exception as e:
                    logger.warning(f"Error processing Tabula table {idx}: {e}")
                    continue
            
            logger.info(f"✓ Extracted {len(tables)} tables using Tabula")
        except Exception as e:
            logger.warning(f"Tabula extraction failed: {e}")
        
        return tables
    
    def extract_tables(self, pdf_path: str, method: str = 'auto', 
                       page_range: Optional[tuple[int, int]] = None) -> List[Table]:
        """Extract tables using best available method"""
        tables = []
        
        if method == 'auto':
            # Try methods in order of accuracy
            if CAMELOT_AVAILABLE:
                tables = self.extract_tables_camelot(pdf_path, page_range)
            
            if not tables and TABULA_AVAILABLE:
                tables = self.extract_tables_tabula(pdf_path, page_range)
            
            if not tables:
                tables = self.extract_tables_pymupdf(pdf_path, page_range)
        elif method == 'camelot' and CAMELOT_AVAILABLE:
            tables = self.extract_tables_camelot(pdf_path, page_range)
        elif method == 'tabula' and TABULA_AVAILABLE:
            tables = self.extract_tables_tabula(pdf_path, page_range)
        else:
            tables = self.extract_tables_pymupdf(pdf_path, page_range)
        
        return tables

# ==========================================
# PAGE-BY-PAGE TEXT EXTRACTOR
# ==========================================
class PageByPageExtractor:
    """Extract text page-by-page to handle large PDFs efficiently"""
    
    def __init__(self, use_marker: bool = True):
        self.use_marker = use_marker and MARKER_AVAILABLE
        self.marker_converter = None
        
        if self.use_marker:
            try:
                marker_config = {
                    "ocr": "easyocr",
                    "languages": ["ar", "en"],
                    "batch_size": 4,  # Reduced for memory efficiency
                    "ocr_all_pages": True,
                    "force_ocr": True
                }
                self.marker_converter = PdfConverter(
                    artifact_dict=create_model_dict(),
                    config=marker_config
                )
                logger.info("✓ Marker initialized for page-by-page extraction")
            except Exception as e:
                logger.warning(f"Marker init failed: {e}")
                self.marker_converter = None
    
    def extract_text_pymupdf(self, pdf_path: str, batch_size: int = 50) -> str:
        """Extract text using PyMuPDF in batches"""
        logger.info(f"Extracting text with PyMuPDF (batch size: {batch_size})")
        
        doc = fitz.open(pdf_path)
        total_pages = len(doc)
        text_parts = []
        
        for batch_start in range(0, total_pages, batch_size):
            batch_end = min(batch_start + batch_size, total_pages)
            logger.info(f"Processing pages {batch_start+1} to {batch_end}...")
            
            batch_text = []
            for page_num in range(batch_start, batch_end):
                try:
                    page = doc[page_num]
                    page_text = page.get_text()
                    batch_text.append(page_text)
                except Exception as e:
                    logger.warning(f"Error extracting page {page_num+1}: {e}")
                    batch_text.append("")
            
            text_parts.append("\n\n".join(batch_text))
        
        doc.close()
        full_text = "\n\n".join(text_parts)
        logger.info(f"✓ Extracted {len(full_text)} characters from {total_pages} pages")
        return full_text
    
    def extract_text_pymupdf4llm(self, pdf_path: str) -> str:
        """Extract text using pymupdf4llm - with memory management"""
        if not PYMUPDF4LLM_AVAILABLE:
            logger.warning("PyMuPDF4LLM not available, using PyMuPDF")
            return self.extract_text_pymupdf(pdf_path)
        
        logger.info("Extracting text with PyMuPDF4LLM...")
        try:
            text = pymupdf4llm.to_markdown(pdf_path)
            logger.info(f"✓ Extracted {len(text)} characters")
            return text
        except MemoryError:
            logger.warning("MemoryError with pymupdf4llm, falling back to batch processing")
            return self.extract_text_pymupdf(pdf_path)
        except Exception as e:
            logger.warning(f"pymupdf4llm failed: {e}, using PyMuPDF")
            return self.extract_text_pymupdf(pdf_path)
    
    def extract_text_marker(self, pdf_path: str, page_batch_size: int = 20) -> str:
        """Extract text using Marker in batches for large PDFs"""
        if not self.marker_converter:
            logger.warning("Marker not available, falling back")
            return self.extract_text_pymupdf4llm(pdf_path)
        
        logger.info(f"Extracting text with Marker (batch size: {page_batch_size})")
        
        try:
            # For large PDFs, process in batches
            doc = fitz.open(pdf_path)
            total_pages = len(doc)
            doc.close()
            
            if total_pages > 100:
                logger.info(f"Large PDF detected ({total_pages} pages), using batch processing")
                return self._extract_marker_batched(pdf_path, total_pages, page_batch_size)
            else:
                # Small PDF - process normally
                result = self.marker_converter(pdf_path)
                logger.info(f"✓ Extracted {len(result.markdown)} characters")
                return result.markdown
        
        except MemoryError:
            logger.warning("MemoryError with Marker, falling back to PyMuPDF")
            return self.extract_text_pymupdf(pdf_path)
        except Exception as e:
            logger.warning(f"Marker failed: {e}, using fallback")
            return self.extract_text_pymupdf4llm(pdf_path)
    
    def _extract_marker_batched(self, pdf_path: str, total_pages: int, 
                                batch_size: int) -> str:
        """Extract large PDF with Marker in batches"""
        # For very large PDFs, fall back to simpler extraction
        logger.warning(f"PDF has {total_pages} pages - using PyMuPDF for stability")
        return self.extract_text_pymupdf(pdf_path, batch_size=batch_size)
    
    def extract_text(self, pdf_path: str, method: str = 'auto') -> tuple[str, str]:
        """Extract text using best available method - returns (text, method_used)"""
        
        if method == 'marker' and self.use_marker:
            return self.extract_text_marker(pdf_path), 'marker'
        elif method == 'pymupdf4llm' and PYMUPDF4LLM_AVAILABLE:
            return self.extract_text_pymupdf4llm(pdf_path), 'pymupdf4llm'
        elif method == 'pymupdf':
            return self.extract_text_pymupdf(pdf_path), 'pymupdf'
        else:
            # Auto mode - try best method first
            if self.use_marker:
                try:
                    return self.extract_text_marker(pdf_path), 'marker'
                except:
                    pass
            
            if PYMUPDF4LLM_AVAILABLE:
                try:
                    return self.extract_text_pymupdf4llm(pdf_path), 'pymupdf4llm'
                except:
                    pass
            
            return self.extract_text_pymupdf(pdf_path), 'pymupdf'

# ==========================================
# PDF METADATA EXTRACTOR
# ==========================================
class PDFMetadataExtractor:
    """Extract metadata from PDF efficiently"""
    
    @staticmethod
    def extract_metadata(pdf_path: str) -> Dict:
        """Extract PDF metadata"""
        try:
            doc = fitz.open(pdf_path)
            metadata = {
                'page_count': len(doc),
                'file_size': Path(pdf_path).stat().st_size,
                'title': doc.metadata.get('title', ''),
                'author': doc.metadata.get('author', ''),
                'subject': doc.metadata.get('subject', ''),
                'creator': doc.metadata.get('creator', ''),
                'producer': doc.metadata.get('producer', ''),
                'creation_date': doc.metadata.get('creationDate', ''),
            }
            doc.close()
            return metadata
        except Exception as e:
            logger.warning(f"Error extracting metadata: {e}")
            return {'page_count': 0, 'file_size': 0}
    
    @staticmethod
    def detect_language(text: str) -> tuple[str, float]:
        """Detect document language"""
        try:
            import regex
            arabic_chars = len(regex.findall(r'[\u0600-\u06FF]', text))
            total_chars = len(regex.findall(r'[a-zA-Z\u0600-\u06FF]', text))
            
            if total_chars == 0:
                return 'unknown', 0.0
            
            arabic_ratio = arabic_chars / total_chars
            
            if arabic_ratio > 0.3:
                return 'arabic', arabic_ratio
            elif arabic_ratio > 0.1:
                return 'mixed', arabic_ratio
            else:
                return 'english', 1.0 - arabic_ratio
        except:
            return 'unknown', 0.0

# ==========================================
# ENHANCED RAG EXTRACTOR - MAIN CLASS
# ==========================================
class EnhancedRAGExtractor:
    """Main extractor optimized for RAG with large PDF support"""
   
    def __init__(self, remove_markdown: bool = True, use_marker: bool = True):
        self.processor = ArabicTextProcessor()
        self.detector = SectionDetector()
        self.chunker = SmartChunker()
        self.table_extractor = TableExtractor()
        self.text_extractor = PageByPageExtractor(use_marker=use_marker)
        self.metadata_extractor = PDFMetadataExtractor()
        self.remove_markdown = remove_markdown
        
        logger.info("✓ EnhancedRAGExtractor initialized")

    def extract(self, 
                pdf_path: str, 
                max_chunk_size: int = 1000,
                chunk_overlap: int = 100,
                extract_tables: bool = True, 
                table_method: str = 'auto',
                text_method: str = 'auto',
                process_in_batches: bool = True) -> DocumentOutput:
        """
        Extract and structure PDF for RAG with tables
        
        Args:
            pdf_path: Path to PDF file
            max_chunk_size: Maximum words per chunk
            chunk_overlap: Word overlap between chunks
            extract_tables: Whether to extract tables
            table_method: 'auto', 'camelot', 'tabula', or 'pymupdf'
            text_method: 'auto', 'marker', 'pymupdf4llm', or 'pymupdf'
            process_in_batches: Use batch processing for large PDFs
        """
        logger.info(f"=" * 60)
        logger.info(f"Processing: {Path(pdf_path).name}")
        logger.info(f"=" * 60)
        
        # Step 1: Extract metadata
        logger.info("📋 Step 1: Extracting metadata...")
        pdf_metadata = self.metadata_extractor.extract_metadata(pdf_path)
        page_count = pdf_metadata['page_count']
        file_size_mb = pdf_metadata['file_size'] / (1024 * 1024)
        
        logger.info(f"✓ PDF Info: {page_count} pages, {file_size_mb:.2f} MB")
        
        # Determine if we need special handling for large PDFs
        is_large_pdf = page_count > 100 or file_size_mb > 50
        
        if is_large_pdf:
            logger.warning(f"⚠️  Large PDF detected - enabling optimizations")
            process_in_batches = True
        
        # Step 2: Extract raw text
        logger.info("📄 Step 2: Extracting text...")
        try:
            raw_text, method_used = self.text_extractor.extract_text(
                pdf_path, 
                method=text_method
            )
            logger.info(f"✓ Text extracted using {method_used} ({len(raw_text)} chars)")
        except Exception as e:
            logger.error(f"Text extraction failed: {e}")
            raise
        
        # Check if we got text
        if not raw_text or len(raw_text.strip()) < 100:
            logger.warning("⚠️  Very little text extracted, PDF might be image-based")
        
        # Step 3: Extract tables
        tables = []
        if extract_tables:
            logger.info("📊 Step 3: Extracting tables...")
            try:
                if process_in_batches and is_large_pdf:
                    # Process tables in page batches for large PDFs
                    tables = self._extract_tables_batched(
                        pdf_path, page_count, table_method
                    )
                else:
                    tables = self.table_extractor.extract_tables(
                        pdf_path, method=table_method
                    )
                logger.info(f"✓ Extracted {len(tables)} tables")
            except Exception as e:
                logger.warning(f"Table extraction failed: {e}")
                tables = []
        else:
            logger.info("📊 Step 3: Skipping table extraction (disabled)")
        
        # Step 4: Clean text
        logger.info("🧹 Step 4: Cleaning Arabic text...")
        try:
            if is_large_pdf and len(raw_text) > 100000:
                # Use streaming cleaning for very large texts
                cleaned_text = self.processor.clean_text_streaming(
                    raw_text, 
                    chunk_size=50000,
                    remove_markdown=self.remove_markdown
                )
            else:
                cleaned_text = self.processor.clean_text(
                    raw_text, 
                    remove_markdown=self.remove_markdown
                )
            
            reduction = 100 * (1 - len(cleaned_text) / max(len(raw_text), 1))
            logger.info(f"✓ Text cleaned: {len(raw_text)} → {len(cleaned_text)} chars ({reduction:.1f}% reduction)")
        except Exception as e:
            logger.error(f"Text cleaning failed: {e}")
            cleaned_text = raw_text
        
        # Step 5: Detect language
        logger.info("🌐 Step 5: Detecting language...")
        language, lang_ratio = self.metadata_extractor.detect_language(cleaned_text)
        logger.info(f"✓ Language: {language} (confidence: {lang_ratio:.1%})")
       
        # Step 6: Extract sections
        logger.info("📑 Step 6: Extracting sections...")
        try:
            sections = self.detector.extract_sections(cleaned_text, language)
            logger.info(f"✓ Found {len(sections)} sections")
            
            # Add page numbers to sections (estimate based on position)
            self._assign_page_numbers(sections, page_count, len(cleaned_text))
        except Exception as e:
            logger.error(f"Section extraction failed: {e}")
            # Create single section as fallback
            sections = [Section(
                id="section_001",
                title="المحتوى" if language == 'arabic' else "Content",
                content=cleaned_text,
                level=1,
                page_start=1,
                page_end=page_count,
                word_count=len(cleaned_text.split()),
                char_count=len(cleaned_text)
            )]
       
        # Step 7: Detect lists in sections
        logger.info("📝 Step 7: Detecting lists...")
        for section in sections:
            if any(line.strip().startswith(('- ', '• ', '* ', '1.', '2.')) 
                   for line in section.content.split('\n')):
                section.has_list = True
        
        # Step 8: Link tables to sections
        logger.info("🔗 Step 8: Linking tables to sections...")
        for table in tables:
            for section in sections:
                if section.page_start <= table.page_number <= section.page_end:
                    section.has_table = True
                    table.section_id = section.id
                    break
       
        # Step 9: Create chunks
        logger.info("✂️  Step 9: Creating chunks...")
        try:
            chunks = self.chunker.chunk_by_sections(
                sections, 
                max_chunk_size=max_chunk_size,
                overlap=chunk_overlap
            )
            logger.info(f"✓ Created {len(chunks)} chunks")
            
            # Verify chunk sizes
            oversized = [c for c in chunks if len(c.content.split()) > max_chunk_size * 1.1]
            if oversized:
                logger.warning(f"⚠️  {len(oversized)} chunks exceed target size")
        except Exception as e:
            logger.error(f"Chunking failed: {e}")
            raise
       
        # Step 10: Extract title
        logger.info("📌 Step 10: Extracting title...")
        title = self._extract_title(cleaned_text, pdf_metadata, pdf_path)
        logger.info(f"✓ Title: {title}")
       
        # Create final output
        doc_output = DocumentOutput(
            title=title,
            language=language,
            total_pages=page_count,
            sections=sections,
            tables=tables,
            chunks=chunks,
            full_text=cleaned_text,
            metadata={
                'source_file': str(pdf_path),
                'file_size_mb': file_size_mb,
                'extraction_method': method_used,
                'table_extraction_method': table_method if extract_tables else 'none',
                'extraction_date': datetime.now().isoformat(),
                'total_sections': len(sections),
                'total_chunks': len(chunks),
                'total_tables': len(tables),
                'language': language,
                'language_confidence': lang_ratio,
                'is_large_pdf': is_large_pdf,
                'processed_in_batches': process_in_batches,
                'chunk_size': max_chunk_size,
                'chunk_overlap': chunk_overlap,
                **pdf_metadata
            }
        )
        
        # Log summary
        logger.info("=" * 60)
        logger.info("✅ EXTRACTION COMPLETE")
        logger.info(f"   Sections: {len(sections)}")
        logger.info(f"   Tables: {len(tables)}")
        logger.info(f"   Chunks: {len(chunks)}")
        logger.info(f"   Total words: {sum(s.word_count for s in sections):,}")
        logger.info("=" * 60)
        
        return doc_output
    
    def _extract_tables_batched(self, pdf_path: str, total_pages: int, 
                                method: str) -> list:
        """Extract tables in batches for large PDFs"""
        tables = []
        batch_size = 50
        
        for start in range(0, total_pages, batch_size):
            end = min(start + batch_size, total_pages)
            logger.info(f"  Extracting tables from pages {start+1} to {end}")
            
            try:
                batch_tables = self.table_extractor.extract_tables(
                    pdf_path, 
                    method=method, 
                    page_range=(start, end)
                )
                tables.extend(batch_tables)
            except Exception as e:
                logger.warning(f"  Batch {start}-{end} failed: {e}")
        
        return tables
    
    def _assign_page_numbers(self, sections: list, total_pages: int, 
                            total_chars: int):
        """Estimate page numbers for sections based on position"""
        if not sections:
            return
        
        current_chars = 0
        for section in sections:
            # Estimate start page
            section.page_start = max(1, int((current_chars / total_chars) * total_pages) + 1)
            current_chars += section.char_count
            section.page_end = min(total_pages, int((current_chars / total_chars) * total_pages) + 1)
    
    def _extract_title(self, text: str, metadata: dict, pdf_path: str) -> str:
        """Extract document title"""
        # Try metadata first
        if metadata.get('title'):
            return metadata['title'].strip()
        
        # Try first meaningful line
        try:
            import regex
            title_match = regex.search(r'^([^\n]{10,100})', text, regex.MULTILINE)
            if title_match:
                return title_match.group(1).strip()
        except:
            pass
        
        # Fallback to filename
        return Path(pdf_path).stem

# ==========================================
# OUTPUT FORMATTERS
# ==========================================
def save_for_rag(doc_output: DocumentOutput, output_dir: Path):
    """Save in RAG-optimized formats including tables"""
    output_dir.mkdir(parents=True, exist_ok=True)
    base_name = Path(doc_output.metadata['source_file']).stem

    logger.info(f"💾 Saving outputs to: {output_dir}")

    # 1. Chunks JSON (most important for RAG)
    chunks_file = output_dir / f"{base_name}_chunks.json"
    with open(chunks_file, 'w', encoding='utf-8') as f:
        json.dump([c.to_dict() for c in doc_output.chunks], f, ensure_ascii=False, indent=2)
    logger.info(f"  ✓ Chunks: {chunks_file.name}")

    # 2. Clean full text
    txt_file = output_dir / f"{base_name}_clean.txt"
    with open(txt_file, 'w', encoding='utf-8') as f:
        f.write(doc_output.full_text)
    logger.info(f"  ✓ Text: {txt_file.name}")

    # 3. Sections JSON
    sections_file = output_dir / f"{base_name}_sections.json"
    with open(sections_file, 'w', encoding='utf-8') as f:
        json.dump([s.to_dict() for s in doc_output.sections], f, ensure_ascii=False, indent=2)
    logger.info(f"  ✓ Sections: {sections_file.name}")

    # 4. Tables (multiple formats)
    if doc_output.tables:
        # JSON
        tables_file = output_dir / f"{base_name}_tables.json"
        with open(tables_file, 'w', encoding='utf-8') as f:
            json.dump([t.to_dict() for t in doc_output.tables], f, ensure_ascii=False, indent=2)
        logger.info(f"  ✓ Tables JSON: {tables_file.name}")
        
        # Markdown
        tables_md_file = output_dir / f"{base_name}_tables.md"
        with open(tables_md_file, 'w', encoding='utf-8') as f:
            for table in doc_output.tables:
                f.write(f"\n## {table.caption}\n\n")
                f.write(table.to_markdown())
                f.write("\n\n---\n")
        logger.info(f"  ✓ Tables MD: {tables_md_file.name}")
        
        # CSV
        tables_csv_dir = output_dir / f"{base_name}_tables_csv"
        tables_csv_dir.mkdir(exist_ok=True)
        for table in doc_output.tables:
            csv_file = tables_csv_dir / f"{table.id}.csv"
            with open(csv_file, 'w', encoding='utf-8') as f:
                f.write(table.to_csv())
        logger.info(f"  ✓ Tables CSV: {tables_csv_dir.name}/")

    # 5. Full structured output
    full_file = output_dir / f"{base_name}_full.json"
    with open(full_file, 'w', encoding='utf-8') as f:
        json.dump(doc_output.to_dict(), f, ensure_ascii=False, indent=2)
    logger.info(f"  ✓ Full output: {full_file.name}")
    
    # 6. Summary stats
    stats_file = output_dir / f"{base_name}_stats.json"
    with open(stats_file, 'w', encoding='utf-8') as f:
        stats = doc_output.get_summary_stats()
        json.dump(stats, f, ensure_ascii=False, indent=2)
    logger.info(f"  ✓ Statistics: {stats_file.name}")

# ==========================================
# MAIN EXECUTION
# ==========================================
def process_pdf(pdf_path: Path, 
                output_dir: Path, 
                chunk_size: int = 1000,
                chunk_overlap: int = 100,
                extract_tables: bool = True, 
                table_method: str = 'auto',
                text_method: str = 'auto',
                use_marker: bool = True):
    """Process a single PDF"""
    try:
        extractor = EnhancedRAGExtractor(use_marker=use_marker)
        doc_output = extractor.extract(
            str(pdf_path), 
            max_chunk_size=chunk_size,
            chunk_overlap=chunk_overlap,
            extract_tables=extract_tables,
            table_method=table_method,
            text_method=text_method
        )
        save_for_rag(doc_output, output_dir)
        logger.info(f"✅ SUCCESS: {pdf_path.name}")
        return True
    except Exception as e:
        logger.error(f"❌ FAILED: {pdf_path.name} - {e}")
        import traceback
        traceback.print_exc()
        return False

def main():
    """Main CLI entry point"""
    import argparse
    
    parser = argparse.ArgumentParser(
        description="Enhanced Arabic PDF Extractor for RAG with Large PDF Support",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Process single PDF
  python part3_main.py input.pdf output_dir/
  
  # Process with custom chunk size
  python part3_main.py input.pdf output_dir/ --chunk-size 1500
  
  # Skip tables
  python part3_main.py input.pdf output_dir/ --no-tables
  
  # Use specific extraction method
  python part3_main.py input.pdf output_dir/ --text-method pymupdf
  
  # Process directory
  python part3_main.py input_dir/ output_dir/
        """
    )
    
    parser.add_argument("input", help="Input PDF file or directory")
    parser.add_argument("output", help="Output directory")
    parser.add_argument("--chunk-size", type=int, default=1000, 
                       help="Max words per chunk (default: 1000)")
    parser.add_argument("--chunk-overlap", type=int, default=100,
                       help="Word overlap between chunks (default: 100)")
    parser.add_argument("--no-tables", action="store_true", 
                       help="Skip table extraction")
    parser.add_argument("--table-method", choices=['auto', 'camelot', 'tabula', 'pymupdf'],
                       default='auto', help="Table extraction method")
    parser.add_argument("--text-method", choices=['auto', 'marker', 'pymupdf4llm', 'pymupdf'],
                       default='auto', help="Text extraction method")
    parser.add_argument("--no-marker", action="store_true",
                       help="Disable Marker even if available")
    parser.add_argument("--verbose", "-v", action="store_true",
                       help="Enable verbose logging")
    
    args = parser.parse_args()
    
    # Set logging level
    if args.verbose:
        logging.getLogger().setLevel(logging.DEBUG)
    
    # Prepare paths
    input_path = Path(args.input)
    output_path = Path(args.output)
    output_path.mkdir(parents=True, exist_ok=True)
    
    extract_tables = not args.no_tables
    use_marker = not args.no_marker
    
    # Process
    if input_path.is_file():
        logger.info("Processing single PDF...")
        success = process_pdf(
            input_path, output_path, 
            chunk_size=args.chunk_size,
            chunk_overlap=args.chunk_overlap,
            extract_tables=extract_tables,
            table_method=args.table_method,
            text_method=args.text_method,
            use_marker=use_marker
        )
        exit(0 if success else 1)
    else:
        # Process directory
        pdfs = list(input_path.rglob("*.pdf"))
        logger.info(f"Found {len(pdfs)} PDFs to process")
        
        successful = 0
        failed = 0
        
        for idx, pdf in enumerate(pdfs, 1):
            logger.info(f"\n{'='*60}")
            logger.info(f"Processing {idx}/{len(pdfs)}: {pdf.name}")
            logger.info(f"{'='*60}")
            
            success = process_pdf(
                pdf, output_path,
                chunk_size=args.chunk_size,
                chunk_overlap=args.chunk_overlap,
                extract_tables=extract_tables,
                table_method=args.table_method,
                text_method=args.text_method,
                use_marker=use_marker
            )
            
            if success:
                successful += 1
            else:
                failed += 1
        
        logger.info(f"\n{'='*60}")
        logger.info(f"BATCH COMPLETE: {successful} successful, {failed} failed")
        logger.info(f"{'='*60}")
        
        exit(0 if failed == 0 else 1)

if __name__ == "__main__":
    main()