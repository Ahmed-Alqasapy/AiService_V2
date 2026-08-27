# download_reranker.py
# شغّل هذا الملف مرة واحدة فقط لحفظ jina-reranker-v3 محليًا

from transformers import AutoTokenizer, AutoModelForSequenceClassification
import os

# المسار اللي عايز تحفظ فيه الموديل داخل مشروعك
SAVE_PATH = r"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB\Hybird_LOGIN_JINA\AiService\jina-reranker-v3"

# لو المجلد مش موجود نعمل واحد جديد
os.makedirs(SAVE_PATH, exist_ok=True)

print("جاري تحميل jina-reranker-v3 من Hugging Face... (حوالي 2.1 جيجا، أول مرة بس)")
print("هيتم الحفظ في:", SAVE_PATH)

# تحميل وحفظ الـ tokenizer والموديل
tokenizer = AutoTokenizer.from_pretrained(
    "jinaai/jina-reranker-v3",
    trust_remote_code=True
)
model = AutoModelForSequenceClassification.from_pretrained(
    "jinaai/jina-reranker-v3",
    trust_remote_code=True,
    torch_dtype="auto"  # float16 على GPU، float32 على CPU
)

# حفظ الموديل والـ tokenizer محليًا
tokenizer.save_pretrained(SAVE_PATH)
model.save_pretrained(SAVE_PATH)

print("تم بنجاح!")
print("دلوقتي تقدر تشغل السيرفر أوفلاين 100% من المجلد ده:")
print(SAVE_PATH)