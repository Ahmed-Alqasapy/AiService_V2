# extract.py → النسخة الذهبية النهائية 2025 (تعمل + تنظيف header + صفحات واضحة)
import easyocr
from pdf2image import convert_from_path
from pathlib import Path
import sys
import numpy as np
import cv2
import re

print("جاري تحميل EasyOCR (العربية + الإنجليزية)...")
reader = easyocr.Reader(['ar', 'en'], gpu=False)

# === الهيدر والفوتر الكويتي اللي لازم نمسحه عشان الـ RAG يبقى مثالي ===
HEADER_FOOTER_PATTERNS = [
    "وزارة التجارة", "ministry of commerce", "دولة الكويت", "state of kuwait",
    "مركز الكويت للأعمال", "kuwait business center", "الدليل الإرشادي", "صفحة", "page",
    "paci", "public authority", "--------------------------------", "=============="
]

def is_header_footer(line):
    line = line.strip().lower()
    if len(line) < 15:
        return True
    return any(pat in line for pat in HEADER_FOOTER_PATTERNS)

def fix_broken_numbers(text):
    text = re.sub(r'(\d)\s+(\d)', r'\1\2', text)
    text = re.sub(r'([\u0660-\u0669])\s+([\u0660-\u0669])', lambda m: m.group(1)+m.group(2), text)
    text = re.sub(r'(\d)\s*\.\s*(\d)', r'\1.\2', text)
    text = re.sub(r'(\d)\s*,\s*(\d{3})', r'\1,\2', text)
    return text

def extract_text_from_pdf(pdf_path):
    pdf_path = Path(pdf_path)
    print(f"معالجة الملف: {pdf_path.name}")

    try:
        images = convert_from_path(str(pdf_path), dpi=400, fmt='png', thread_count=6)
    except Exception as e:
        print(f"خطأ: poppler غير موجود! {e}")
        return

    all_clean_lines = []

    for i, image in enumerate(images, 1):
        print(f"صفحة {i}/{len(images)} → OCR", end="")

        img_np = np.array(image)
        gray = cv2.cvtColor(img_np, cv2.COLOR_RGB2GRAY)
        h, w = gray.shape
        if w < 2000:
            gray = cv2.resize(gray, (w*2, h*2), interpolation=cv2.INTER_CUBIC)

        clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8,8))
        enhanced = clahe.apply(gray)
        denoised = cv2.fastNlMeansDenoising(enhanced, None, 10, 7, 21)
        _, binary = cv2.threshold(denoised, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)

        lines = reader.readtext(
            binary,
            detail=0,
            paragraph=True,
            width_ths=0.7,
            height_ths=0.7,
            text_threshold=0.7,
            low_text=0.3,
            link_threshold=0.4,
            canvas_size=4096,
            mag_ratio=1.5
        )

        page_clean_lines = []
        for line in lines:
            line = line.strip()
            if not line or is_header_footer(line):
                continue
            page_clean_lines.append(line)

        # أهم سطر في التاريخ: <<PAGE: X>>
        if page_clean_lines:
            all_clean_lines.append(f"\n<<PAGE:{i}>>\n")
            all_clean_lines.extend(page_clean_lines)
            all_clean_lines.append("")  # سطر فاضي بين الفقرات

        print(f" → {len(page_clean_lines)} سطر نظيف")

    if not all_clean_lines:
        print("تحذير: ما طلعش نص!")
        return

    final_text = fix_broken_numbers("\n".join(all_clean_lines))

    result_dir = Path("Results")
    result_dir.mkdir(exist_ok=True)
    output_file = result_dir / f"{pdf_path.stem}_extracted.txt"
    output_file.write_text(final_text, encoding="utf-8-sig")

    print(f"\nتم بنجاح 100%!")
    print(f"الملف النظيف: {output_file}")
    print(f"عدد الصفحات: {len(images)} | عدد الأسطر النظيفة: {len([l for l in all_clean_lines if l.strip() and not l.startswith('<<PAGE')])}")

if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("استخدام: python extract.py \"مسار_الملف.pdf\"")
        sys.exit(1)

    pdf_file = sys.argv[1]
    if not Path(pdf_file).exists():
        print(f"الملف غير موجود: {pdf_file}")
        sys.exit(1)

    extract_text_from_pdf(pdf_file)
    print("انتهى بنجاح!")