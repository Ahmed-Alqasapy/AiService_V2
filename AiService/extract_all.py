# extract_all_from_uploads.py
# يعالج كل ملفات PDF في مجلد Uploads تلقائيًا (نصي أو مسحوب)
# يحفظ الجداول كنص + إكسل في مجلد Results

from pdf2image import convert_from_path
import requests
import base64
import json
import os
from pathlib import Path

# مجلدات
UPLOADS_DIR = Path("Uploads")
RESULTS_DIR = Path("Results_all")
RESULTS_DIR.mkdir(exist_ok=True)
(EXCEL_DIR := RESULTS_DIR / "Excel").mkdir(exist_ok=True)
(TEXT_DIR := RESULTS_DIR / "Text").mkdir(exist_ok=True)

# البرومبت الأقوى لجداول الكويت (يشتغل على النصي والمسحوب)
PROMPT_FULL_EXTRACTION = """استخرج كل النصوص والجداول من الصفحة.
- للنصوص العادية: اكتبها كنص عادي.
- للجداول: اكتبها كنص منسق بتبويب (tab-separated) داخل علامات [جدول] و[/جدول]
- احتفظ بالترتيب الطبيعي للقراءة من اليمين لليسار والأعلى للأسفل.
- احتفظ بالعربي 100% بدون تغيير أي حرف.

مثال:
[جدول]
السلسل	اسم الخدمة	نوع الخدمة	الرسوم
1	تأسيس شركة توصية بسيطة	إلكترونياً	لا يوجد
[/جدول]

نص توضيحي هنا...
"""

def pdf_to_base64_images(pdf_path, dpi=300):
    images = convert_from_path(pdf_path, dpi=dpi)
    base64_images = []
    for img in images:
        import io
        buffered = io.BytesIO()
        img.save(buffered, format="PNG")
        base64_images.append(base64.b64encode(buffered.getvalue()).decode())
    return base64_images

def extract_with_qwen(image_base64):
    url = "http://localhost:11434/api/generate"
    payload = {
        "model": "qwen3-vl:2b",        # أو qwen2.5-vl:7b
        "prompt": PROMPT_FULL_EXTRACTION,
        "images": [image_base64],
        "stream": False
    }
    response = requests.post(url, json=payload, timeout=300)
    response.raise_for_status()
    return response.json()["response"].strip()

def save_as_excel(table_text, output_path):
    lines = [line.split("\t") for line in table_text.strip().splitlines() if line.strip()]
    if len(lines) < 2:
        return False
    import pandas as pd
    df = pd.DataFrame(lines[1:], columns=lines[0])
    df.to_excel(output_path, index=False)
    return True

def process_pdf(pdf_path):
    print(f"\nمعالجة: {pdf_path.name}")
    images = pdf_to_base64_images(pdf_path)
    all_tables_text = []
    table_count = 0

    for i, b64 in enumerate(images, 1):
        print(f"  صفحة {i}/{len(images)}...", end="")
        try:
            result = extract_with_qwen(b64)
            if "لا يوجد جدول" not in result and len(result.strip()) > 20:
                table_count += 1
                page_block = f"[صفحة {i}]\n{result}\n"
                all_tables_text.append(page_block)

                # حفظ إكسل لكل صفحة فيها جدول
                excel_file = EXCEL_DIR / f"{pdf_path.stem}_صفحة_{i}.xlsx"
                save_as_excel(result, excel_file)
                print(f" → جدول محفوظ")
            else:
                print(" → لا يوجد جدول")
        except Exception as e:
            print(f" → خطأ: {e}")

    # حفظ النص النهائي
    final_text = "\n\n".join(all_tables_text)
    txt_file = TEXT_DIR / f"{pdf_path.stem}_الجداول.txt"
    txt_file.write_text(final_text, encoding="utf-8-sig")

    print(f"تم → {table_count} جدول")
    print(f"   النص: {txt_file}")
    if table_count > 0:
        print(f"   الإكسل: {EXCEL_DIR}")

# معالجة كل الملفات في Uploads
if __name__ == "__main__":
    if not UPLOADS_DIR.exists():
        print("مجلد Uploads مش موجود!")
    else:
        pdf_files = list(UPLOADS_DIR.rglob("*.pdf"))
        if not pdf_files:
            print("ما لقيتش PDF في Uploads")
        else:
            print(f"لقيت {len(pdf_files)} ملف PDF في Uploads")
            for pdf in pdf_files:
                process_pdf(pdf)
            print("\nخلّصنا كل الملفات!")
            print(f"النتايج في مجلد: {RESULTS_DIR}")