import easyocr
from pdf2image import convert_from_path
from pathlib import Path
import sys
import numpy as np
import cv2
import re
from typing import List, Tuple, Dict
from collections import defaultdict

# Try to import pyarabic for better Arabic text correction
try:
    import pyarabic.araby as araby
    PYARABIC_AVAILABLE = True
    print("✅ pyarabic loaded - Arabic correction enabled")
except ImportError:
    PYARABIC_AVAILABLE = False
    print("⚠️  pyarabic not found - install with: pip install pyarabic")

print("جاري تحميل EasyOCR (العربية + الإنجليزية)...")
reader = easyocr.Reader(['ar', 'en'], gpu=False)

# Header and footer patterns to remove
HEADER_FOOTER_PATTERNS = [
    "وزارة التجارة", "ministry of commerce", "دولة الكويت", "state of kuwait",
    "مركز الكويت للأعمال", "kuwait business center", "الدليل الإرشادي",
    "paci", "public authority"
]

# Common OCR errors dictionary
COMMON_FIXES = {
    'الدير': 'المدير',
    'الحال': 'المال',
    'احال': 'المال',
    'اطال': 'المال',
    'الحدودة': 'المحدودة',
    'السؤوليه': 'المسؤولية',
    'الستفيد': 'المستفيد',
    'الستفيب': 'المستفيد',
    'مذير': 'مدير',
    'عخ': 'عن',
    'غحال': 'في حال',
    'لوقع': 'لموقع',
    'خول': 'الدخول',
    'الذكياة': 'الذكية',
    'طبافء': 'طباعة',
    'الظلبات': 'الطلبات',
    'فستخدم': 'مستخدم',
    'عاد': 'عدد',
    'الاعمال': 'الأعمال',
    'الداخلياء': 'الداخلية',
    'تموافقه': 'بموافقة',
    'ارفاق': 'إرفاق',
    'اويمكن': 'أو يمكن',
    'الجا': 'التجارة',
    'اطدير': 'المدير',
    'الدفية': 'المدنية',
    'النزل': 'المنزل',
    'للاعمال': 'للأعمال',
    'مركزالكويت': 'مركز الكويت',
    'التجا': 'التجاري',
    'افراده': 'أفراد',
    'وئيسي': 'رئيسي',
    'الوجودة': 'الموجودة',
    'اجد': 'أحد',
    'تتتخلام': 'الاستعلام',
    'الالكتروثية': 'الالكترونية',
    'عباره': 'عبارة',
    'ويتبعاه': 'ويتبعه',
    'الحالء': 'الحال',
    'بيانات': 'البيانات',
}

def is_header_footer(line: str) -> bool:
    """Check if line is a header/footer that should be removed"""
    line = line.strip().lower()
    
    # Very short lines are likely page numbers or separators
    if len(line) < 8:
        return True
    
    # Check for header/footer patterns
    if any(pat in line for pat in HEADER_FOOTER_PATTERNS):
        return True
    
    # Check for page numbers like "صفحة 5" or "page 5"
    if re.match(r'^(صفحة|page)\s*\d+$', line, re.IGNORECASE):
        return True
    
    # Lines that are just separators
    if re.match(r'^[-=_\s]{3,}$', line):
        return True
    
    return False

def normalize_with_pyarabic(text: str) -> str:
    """Normalize Arabic text using pyarabic"""
    if not PYARABIC_AVAILABLE:
        return text
    
    try:
        # Strip diacritics (tashkeel)
        text = araby.strip_tashkeel(text)
        
        # Normalize hamza forms (أ إ آ → ا)
        text = araby.normalize_hamza(text)
        
        # Normalize ligatures
        text = araby.normalize_ligature(text)
        
        # Strip tatweel (elongation character ـ)
        text = araby.strip_tatweel(text)
        
        return text
    except Exception as e:
        print(f"⚠️  pyarabic normalization warning: {e}")
        return text

def fix_common_ocr_errors(text: str) -> str:
    """Fix common OCR errors using dictionary"""
    for wrong, correct in COMMON_FIXES.items():
        # Use word boundaries to avoid partial replacements
        text = re.sub(r'\b' + re.escape(wrong) + r'\b', correct, text)
    return text

def fix_merged_words(text: str) -> str:
    """Fix words that are merged together"""
    replacements = [
        (r'(مركز)(الكويت)', r'\1 \2'),
        (r'(بوابه)(مركز)', r'\1 \2'),
        (r'(وزارة)(العدل|الداخلية|التجارة)', r'\1 \2'),
        (r'(السجل)(التجاري)', r'\1 \2'),
        (r'(عقد|طلب)(التأسيس)', r'\1 \2'),
        (r'(شركه)(اشخاص|اعمال)', r'\1 \2'),
    ]
    
    for pattern, replacement in replacements:
        text = re.sub(pattern, replacement, text)
    
    return text

def fix_broken_arabic_article(text: str) -> str:
    """Fix broken definite article (ا ل → ال)"""
    # Fix "ا ل" to "ال" before Arabic letters
    text = re.sub(r'\bا\s+ل([أ-ي])', r'ال\1', text)
    return text

def fix_preposition_errors(text: str) -> str:
    """Fix common preposition OCR errors"""
    # Fix "في" (in/at) variations
    text = re.sub(r'\sذ\s', ' في ', text)
    text = re.sub(r'\sيذ\s', ' في ', text)
    text = re.sub(r'\sث\s', ' في ', text)
    
    # Fix "أو" (or)
    text = re.sub(r'\bاو\s', 'أو ', text)
    
    # Fix "عن" (about)
    text = re.sub(r'\bعخ\s', 'عن ', text)
    
    return text

def fix_broken_numbers(text: str) -> str:
    """Fix OCR errors in numbers"""
    # Fix broken Arabic numbers
    text = re.sub(r'([\u0660-\u0669])\s+([\u0660-\u0669])', 
                  lambda m: m.group(1) + m.group(2), text)
    
    # Fix broken English numbers: "1 2 3" → "123"
    text = re.sub(r'(\d)\s+(\d)', r'\1\2', text)
    
    # Fix decimals: "1 . 5" → "1.5"
    text = re.sub(r'(\d)\s*\.\s*(\d)', r'\1.\2', text)
    
    # Fix thousands separator: "1 , 000" → "1,000"
    text = re.sub(r'(\d)\s*,\s*(\d{3})', r'\1,\2', text)
    
    # Fix year formats: "2 0 2 3" → "2023"
    text = re.sub(r'(\d)\s+(\d)\s+(\d)\s+(\d)(?!\d)', r'\1\2\3\4', text)
    
    # Remove leading zeros from Arabic numbers (٠٢٠٢٣ → ٢٠٢٣)
    text = re.sub(r'٠+([١-٩])', r'\1', text)
    
    return text

def fix_punctuation_spacing(text: str) -> str:
    """Fix spacing around punctuation"""
    # Remove space before punctuation
    text = re.sub(r'\s+(\.|\،|\؛)', r'\1', text)
    
    # Add space after punctuation if missing
    text = re.sub(r'(\.|\،|\؛)([^\s\d])', r'\1 \2', text)
    
    # Fix parentheses spacing
    text = re.sub(r'\(\s+', '(', text)
    text = re.sub(r'\s+\)', ')', text)
    
    return text

def apply_all_corrections(text: str) -> str:
    """
    Apply all correction methods in sequence
    This is the main correction pipeline
    """
    # 1. Normalize with pyarabic (if available)
    text = normalize_with_pyarabic(text)
    
    # 2. Fix broken definite article
    text = fix_broken_arabic_article(text)
    
    # 3. Fix common OCR errors
    text = fix_common_ocr_errors(text)
    
    # 4. Fix merged words
    text = fix_merged_words(text)
    
    # 5. Fix prepositions
    text = fix_preposition_errors(text)
    
    # 6. Fix numbers
    text = fix_broken_numbers(text)
    
    # 7. Fix punctuation
    text = fix_punctuation_spacing(text)
    
    # 8. Clean up excessive spaces
    text = re.sub(r' {2,}', ' ', text)
    
    # 9. Clean up excessive newlines (but keep structure)
    text = re.sub(r'\n{4,}', '\n\n\n', text)
    
    return text

def group_text_by_position(results: List[Tuple], page_width: int) -> List[str]:
    """
    Group text blocks by their vertical and horizontal position
    This preserves table/form structure better
    """
    if not results:
        return []
    
    # Group by rows (similar Y coordinates)
    rows = defaultdict(list)
    
    for bbox, text, conf in results:
        if conf < 0.3 or not text.strip():
            continue
        
        # Calculate average Y position
        y_positions = [point[1] for point in bbox]
        avg_y = sum(y_positions) / len(y_positions)
        
        # Calculate X positions for RTL sorting
        x_positions = [point[0] for point in bbox]
        avg_x = sum(x_positions) / len(x_positions)
        max_x = max(x_positions)  # Rightmost point for RTL
        
        # Round Y to group nearby texts
        row_key = round(avg_y / 20) * 20  # Group within 20 pixels
        
        rows[row_key].append((max_x, avg_x, text.strip(), bbox))
    
    # Sort rows by Y position (top to bottom)
    sorted_rows = sorted(rows.items(), key=lambda x: x[0])
    
    # Build output with proper structure
    output_lines = []
    
    for row_y, row_items in sorted_rows:
        # Sort items in row by X position (right to left for Arabic)
        row_items.sort(key=lambda x: x[0], reverse=True)
        
        # Check if this looks like a table row (multiple items)
        if len(row_items) > 1:
            # Combine with some spacing to preserve columns
            line_text = "  |  ".join([item[2] for item in row_items])
        else:
            line_text = row_items[0][2]
        
        output_lines.append(line_text)
    
    return output_lines

def enhance_image(image: np.ndarray) -> np.ndarray:
    """Apply image enhancement for better OCR"""
    # Convert to grayscale
    if len(image.shape) == 3:
        gray = cv2.cvtColor(image, cv2.COLOR_RGB2GRAY)
    else:
        gray = image
    
    # Increase resolution if needed
    h, w = gray.shape
    target_width = 3000  # Higher for forms/tables
    if w < target_width:
        scale = target_width / w
        new_w = int(w * scale)
        new_h = int(h * scale)
        gray = cv2.resize(gray, (new_w, new_h), interpolation=cv2.INTER_CUBIC)
    
    # Apply CLAHE for contrast enhancement
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(12, 12))
    enhanced = clahe.apply(gray)
    
    # Denoise
    denoised = cv2.fastNlMeansDenoising(enhanced, None, h=8, templateWindowSize=7, searchWindowSize=21)
    
    # Sharpen to make text clearer
    kernel = np.array([[-1,-1,-1],
                       [-1, 9,-1],
                       [-1,-1,-1]])
    sharpened = cv2.filter2D(denoised, -1, kernel)
    
    # Adaptive thresholding for better text extraction
    binary = cv2.adaptiveThreshold(
        sharpened, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, 
        cv2.THRESH_BINARY, 15, 5
    )
    
    return binary

def extract_text_from_pdf(pdf_path: str):
    """Extract text from PDF with structure preservation and Arabic correction"""
    pdf_path = Path(pdf_path)
    print(f"\n{'='*70}")
    print(f"📄 معالجة الملف: {pdf_path.name}")
    print(f"{'='*70}\n")

    try:
        print("🔄 تحويل PDF إلى صور بدقة عالية...")
        images = convert_from_path(
            str(pdf_path), 
            dpi=400,  # Higher DPI for forms/tables
            fmt='png', 
            thread_count=4,
            grayscale=False
        )
        print(f"✅ تم تحويل {len(images)} صفحة\n")
    except Exception as e:
        print(f"❌ خطأ: تأكد من تثبيت poppler! {e}")
        return

    all_pages_text = []
    total_lines_extracted = 0

    for page_num, image in enumerate(images, 1):
        print(f"📖 صفحة {page_num}/{len(images)}", end=" → ")
        
        # Convert PIL image to numpy array
        img_np = np.array(image)
        page_width = img_np.shape[1]
        
        # Enhance image
        print("تحسين...", end=" → ")
        enhanced = enhance_image(img_np)
        
        # Perform OCR with detailed output
        print("استخراج...", end=" → ")
        results = reader.readtext(
            enhanced,
            detail=1,  # Get bounding boxes
            paragraph=False,  # We handle structure ourselves
            width_ths=0.7,
            height_ths=0.5,
            text_threshold=0.65,
            low_text=0.35,
            link_threshold=0.4,
            canvas_size=4500,
            mag_ratio=1.8,
            slope_ths=0.1,
            ycenter_ths=0.5,
            height_ths=0.5,
            add_margin=0.15,
            x_ths=1.0,
            y_ths=0.5
        )
        
        # Group text by position to preserve structure
        page_lines = group_text_by_position(results, page_width)
        
        # Filter out headers/footers
        filtered_lines = []
        for line in page_lines:
            if not is_header_footer(line) and line.strip():
                filtered_lines.append(line)
        
        print(f"✓ {len(filtered_lines)} سطر")
        total_lines_extracted += len(filtered_lines)
        
        # Add page marker and content
        if filtered_lines:
            page_text = f"<<PAGE:{page_num}>>\n"
            page_text += "\n".join(filtered_lines)
            page_text += "\n\n"
            all_pages_text.append(page_text)

    if not all_pages_text:
        print("\n⚠️  تحذير: لم يتم استخراج أي نص!")
        return

    # Combine all pages
    final_text = "".join(all_pages_text)
    
    # Apply all Arabic corrections
    print(f"\n🔧 تطبيق التصحيحات على {total_lines_extracted} سطر...")
    original_length = len(final_text)
    final_text = apply_all_corrections(final_text)
    corrections_made = abs(len(final_text) - original_length)
    print(f"✅ تم إجراء ~{corrections_made} تصحيح")
    
    # Save result
    result_dir = Path("Results")
    result_dir.mkdir(exist_ok=True)
    output_file = result_dir / f"{pdf_path.stem}_extracted.txt"
    
    output_file.write_text(final_text, encoding="utf-8-sig")
    
    # Statistics
    total_lines = len([l for l in final_text.split('\n') if l.strip()])
    total_chars = len(final_text)
    total_words = len(final_text.split())
    
    print(f"\n{'='*70}")
    print(f"✅ اكتمل الاستخراج والتصحيح بنجاح!")
    print(f"{'='*70}")
    print(f"📁 الملف المحفوظ: {output_file.name}")
    print(f"📊 الإحصائيات:")
    print(f"   • عدد الصفحات: {len(images)}")
    print(f"   • عدد الأسطر: {total_lines:,}")
    print(f"   • عدد الكلمات: {total_words:,}")
    print(f"   • عدد الأحرف: {total_chars:,}")
    print(f"   • حجم الملف: {output_file.stat().st_size / 1024:.1f} KB")
    print(f"{'='*70}\n")
    
    # Show sample of corrected text
    print("📝 عينة من النص المستخرج (أول 500 حرف):")
    print("─" * 70)
    sample = final_text[:500]
    print(sample)
    if len(final_text) > 500:
        print("\n[...]")
    print("─" * 70 + "\n")
    
    print(f"💾 لعرض النص الكامل، افتح الملف: {output_file}\n")

def main():
    if len(sys.argv) != 2:
        print("\n" + "="*70)
        print("📚 برنامج استخراج النص من PDF مع تصحيح عربي")
        print("="*70)
        print("\n🔧 الاستخدام:")
        print('   python extract.py "path/to/file.pdf"')
        print("\n💡 أمثلة:")
        print('   python extract.py "ministry of Commerce & Industry.pdf"')
        print('   python extract.py "document.pdf"')
        print("\n📦 المتطلبات:")
        print("   pip install easyocr pdf2image opencv-python pillow")
        print("   pip install pyarabic  # اختياري للتصحيح المحسّن")
        print("\n⚙️  ملاحظات:")
        print("   • يدعم PDF متعدد الصفحات")
        print("   • يحافظ على التنسيق والجداول")
        print("   • تصحيح تلقائي للأخطاء الشائعة")
        print("   • النتائج تُحفظ في مجلد Results/")
        print("="*70 + "\n")
        sys.exit(1)

    pdf_file = sys.argv[1]
    pdf_path = Path(pdf_file)
    
    if not pdf_path.exists():
        print(f"\n❌ الملف غير موجود: {pdf_file}\n")
        sys.exit(1)

    extract_text_from_pdf(pdf_file)
    print("✨ انتهى البرنامج بنجاح!\n")

if __name__ == "__main__":
    main()