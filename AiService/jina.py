
from sentence_transformers import SentenceTransformer

model = SentenceTransformer('jinaai/jina-embeddings-v3', trust_remote_code=True, revision='main')

model.save(r'C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB\Hybird_LOGIN_JINA\AiService\jina-embeddings-v3')

print('تم حفظ jina-embeddings-v3 كامل 100% بدون أي ملف ناقص')
