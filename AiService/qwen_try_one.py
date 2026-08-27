
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from transformers import AutoTokenizer, AutoModel, AutoModelForCausalLM
from typing import List, Optional
import torch
import uvicorn
import sys

sys.stdout.reconfigure(encoding='utf-8')

app = FastAPI(
    title=" Reranker عربي محلي 2025 – Qwen3-Reranker-0.6B + Jina v3",
    description="Qwen3-Reranker-0.6B (الطريقة الرسمية) + Jina v3 – 100% Local & Offline – أداء خرافي",
    version="2025.12.GOLDEN"
)

# ============================ مسارات النماذج ============================
EMBEDDING_PATH = r"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB\Hybird_LOGIN_JINA\AiService\jina-embeddings-v3"
RERANKER_PATH   = r"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB\Hybird_LOGIN_JINA\AiService\Embedding-0.6B"

# ============================ تحميل Jina Embeddings ============================
print("جاري تحميل jina-embeddings-v3...")
emb_tokenizer = AutoTokenizer.from_pretrained(EMBEDDING_PATH, trust_remote_code=True, local_files_only=True)
emb_model = AutoModel.from_pretrained(
    EMBEDDING_PATH,
    trust_remote_code=True,
    local_files_only=True,
    torch_dtype=torch.float16,
    device_map="auto"
).eval()
print("Jina-embeddings-v3 جاهز ✓")

# ============================ تحميل Qwen3-Reranker-0.6B بالطريقة الرسمية 100% ============================
print("جاري تحميل Qwen3-Reranker-0.6B (الطريقة الرسمية من Qwen)...")
rerank_tokenizer = AutoTokenizer.from_pretrained(RERANKER_PATH, padding_side="left", trust_remote_code=True)
rerank_model = AutoModelForCausalLM.from_pretrained(
    RERANKER_PATH,
    torch_dtype=torch.float16,
    device_map="auto",
    trust_remote_code=True
).eval()

if rerank_tokenizer.pad_token is None:
    rerank_tokenizer.pad_token = rerank_tokenizer.eos_token

# Token IDs الرسمية (مضمونة في النموذج الأصلي)
token_no  = rerank_tokenizer.convert_tokens_to_ids("no")   # 378
token_yes = rerank_tokenizer.convert_tokens_to_ids("yes")  # 4273

# الـ prefix/suffix الرسمي من Qwen
prefix = "<|im_start|>system\nJudge whether the Document meets the requirements based on the Query and the Instruct provided. Note that the answer can only be \"yes\" or \"no\".<|im_end|>\n<|im_start|>user\n"
suffix = "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"

prefix_tokens = rerank_tokenizer.encode(prefix, add_special_tokens=False)
suffix_tokens = rerank_tokenizer.encode(suffix, add_special_tokens=False)
MAX_INPUT_LEN = 8192

print(f"Qwen3-Reranker-0.6B جاهز 100% ✓ | yes_id={token_yes} | no_id={token_no}")

# ============================ Pydantic Models ============================
class RerankRequest(BaseModel):
    query: str
    passages: List[str]
    top_k: Optional[int] = None

# ============================ دالة بناء الإدخال (الطريقة الرسمية) ============================
def build_input_ids(query: str, document: str) -> list:
    instruction = "Given a web search query, retrieve relevant passages that answer the query"
    text = f"<Instruct>: {instruction}\n<Query>: {query}\n<Document>: {document}"
    tokens = rerank_tokenizer.encode(text, add_special_tokens=False)
    input_ids = prefix_tokens + tokens + suffix_tokens
    if len(input_ids) > MAX_INPUT_LEN:
        input_ids = input_ids[:MAX_INPUT_LEN]
    return input_ids

# ============================ Rerank Endpoint (النسخة الذهبية) ============================
@torch.no_grad()
@app.post("/rerank")
def rerank(request: RerankRequest):
    query = request.query.strip()
    passages = [p.strip() for p in request.passages]
    top_k = request.top_k or len(passages)

    if not passages:
        raise HTTPException(400, "passages list is empty")

    # بناء input_ids لكل passage
    input_ids_list = [build_input_ids(query, doc) for doc in passages]

    # Padding يدوي (أفضل وأسرع من tokenizer.pad)
    max_len = min(max(len(ids) for ids in input_ids_list), MAX_INPUT_LEN)
    padded_ids = []
    attention_mask = []

    for ids in input_ids_list:
        pad_len = max_len - len(ids)
        padded_ids.append([rerank_tokenizer.pad_token_id] * pad_len + ids)
        attention_mask.append([0] * pad_len + [1] * len(ids))

    inputs = {
        "input_ids": torch.tensor(padded_ids, dtype=torch.long).to(rerank_model.device),
        "attention_mask": torch.tensor(attention_mask, dtype=torch.long).to(rerank_model.device)
    }

    # Forward pass → logits من آخر توكن
    logits = rerank_model(**inputs).logits[:, -1, :]

    # استخراج احتمال "yes"
    scores = torch.softmax(logits[:, [token_no, token_yes]], dim=-1)[:, 1]
    scores = scores.detach().float().cpu().numpy()  # ← detach() هنا

    # ترتيب النتائج
    ranked = scores.argsort()[::-1][:top_k]
    results = []
    for rank, idx in enumerate(ranked, 1):
        results.append({
            "rank": rank,
            "index": int(idx),
            "text": passages[idx],
            "relevance_score": round(float(scores[idx]), 6)
        })

    return {"query": query, "total": len(passages), "results": results}

# ============================ Embedding Endpoint (Jina v3) ============================
@app.post("/embed")
def embed(texts: List[str]):
    prompted = [f"Instruct: retrieval.passage\nQuery: {t}" for t in texts]
    inputs = emb_tokenizer(prompted, padding=True, truncation=True, max_length=8192, return_tensors="pt").to(emb_model.device)
    with torch.no_grad():
        embeddings = emb_model(**inputs).last_hidden_state[:, 0, :]
    return {"embeddings": embeddings.float().cpu().tolist()}

# ============================ Root ============================
@app.get("/")
def root():
    return {
        "message": "Qwen3-Reranker-0.6B (Golden Edition) + Jina v3 – شغال 100%",
        "status": "ready",
        "gpu": torch.cuda.is_available(),
        "reranker": "Qwen/Qwen3-Reranker-0.6B (الطريقة الرسمية)"
    }

# ============================ تشغيل السيرفر ============================
if __name__ == "__main__":
    print("\nالسيرفر شغال على http://127.0.0.1:8000")
    print("جرب الاختبار ده:")
    print('''Invoke-RestMethod http://127.0.0.1:8000/rerank -Method Post -ContentType "application/json; charset=utf-8" -Body (@{
    query="من هو رئيس وزراء الكويت حاليًا؟";
    passages=@("أحمد نواف الأحمد الصباح هو رئيس الوزراء","سعر الدولار 0.306")
} | ConvertTo-Json)''')
    uvicorn.run(app, host="127.0.0.1", port=8000, log_level="info")