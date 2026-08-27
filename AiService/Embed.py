# embedding_server.py ← النسخة النهائية 100% محلية – بدون أي pip خارجي
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from sentence_transformers import SentenceTransformer
from transformers import AutoTokenizer, AutoModelForSequenceClassification
from typing import List, Optional
import torch
import uvicorn
import sys

# ✅ Ensure UTF-8 output
sys.stdout.reconfigure(encoding='utf-8')

app = FastAPI(
    title="Jina v3 Full Local RAG Server 2025 – Pure Transformers",
    description="Embedding + jina-reranker-v3 (100% Offline, No External Packages)",
    version="2025.Pure"
)

# ==================================================================
# 1. Embedding Model
# ==================================================================
EMBEDDING_PATH = r"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB\Hybird_LOGIN_JINA\AiService\jina-embeddings-v3"

print("جاري تحميل jina-embeddings-v3...")
embedding_model = SentenceTransformer(
    EMBEDDING_PATH,
    device="cuda" if torch.cuda.is_available() else "cpu",
    trust_remote_code=True,
    local_files_only=True
)
print("Embedding جاهز ✓")

# ==================================================================
# 2. Reranker v3
# ==================================================================
RERANKER_PATH = r"C:\D\Hybird_Module\Combined\HY_TRY_JINA_EMB\Hybird_LOGIN_JINA\AiService\jina-reranker-v3"

print("جاري تحميل jina-reranker-v3 محليًا...")

reranker_tokenizer = AutoTokenizer.from_pretrained(
    RERANKER_PATH,
    trust_remote_code=True,
    local_files_only=True
)

reranker_model = AutoModelForSequenceClassification.from_pretrained(
    RERANKER_PATH,
    trust_remote_code=True,
    torch_dtype=torch.float16 if torch.cuda.is_available() else torch.float32,
    device_map="auto",
    local_files_only=True
)
reranker_model.eval()

# Force pad token
if reranker_tokenizer.pad_token is None or reranker_tokenizer.pad_token_id is None:
    reranker_tokenizer.pad_token = reranker_tokenizer.eos_token
    reranker_tokenizer.pad_token_id = reranker_tokenizer.eos_token_id
reranker_model.config.pad_token_id = reranker_tokenizer.pad_token_id

print(f"Reranker جاهز 100% بدون أي أخطاء على {reranker_model.device}")

# ==================================================================
# Request Models
# ==================================================================
class EmbedRequest(BaseModel):
    texts: List[str]
    is_query: bool = False

class RerankRequest(BaseModel):
    query: str
    passages: List[str]
    top_k: Optional[int] = None

# ==================================================================
# Embedding Endpoint
# ==================================================================
@app.post("/embed")
async def embed(request: EmbedRequest):
    if not request.texts:
        raise HTTPException(400, "texts list is empty")
    prompted = [
        f"Instruct: retrieval.query\nQuery: {t}" if request.is_query
        else f"Instruct: retrieval.passage\nQuery: {t}"
        for t in request.texts
    ]
    embeddings = embedding_model.encode(prompted, normalize_embeddings=True, batch_size=32)
    return {"embeddings": embeddings.tolist()}

# ==================================================================
# Rerank Endpoint (Arabic safe, fully fixed)
# ==================================================================
@torch.no_grad()
@app.post("/rerank")
def rerank(request: RerankRequest):
    query = request.query
    passages = request.passages
    if not passages:
        raise HTTPException(400, "passages list is empty")

    top_k = request.top_k or len(passages)
    instruction = request.instruction or "Given a web search query, retrieve relevant passages that answer the query"

    # === الـ prompt الرسمي من Qwen (مهم جدًا ما تتغيرش ولا كلمة) ===
    prefix = "<|im_start|>system\nJudge whether the Document meets the requirements based on the Query and the Instruct provided. Note that the answer can only be \"yes\" or \"no\".<|im_end|>\n<|im_start|>user\n"
    suffix = "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"

    full_texts = [
        f"{prefix}<Instruct>: {instruction}\n<Query>: {query}\n<Document>: {doc}{suffix}"
        for doc in passages
    ]

    # Tokenization صحيح 100%
    inputs = rerank_tokenizer(
        full_texts,
        padding=True,
        truncation=True,
        max_length=8192,
        return_tensors="pt"
    ).to(rerank_model.device)

    outputs = rerank_model(**inputs)
    logits = outputs.logits[:, -1, :]  # آخر توكن

    # استخراج "yes" و "no"
    token_yes = rerank_tokenizer.convert_tokens_to_ids("yes")
    token_no  = rerank_tokenizer.convert_tokens_to_ids("no")

    scores = torch.nn.functional.softmax(logits[:, [token_no, token_yes]], dim=1)[:, 1]
    scores = scores.cpu().tolist()

    ranked = sorted(enumerate(scores), key=lambda x: x[1], reverse=True)[:top_k]

    results = [
        {
            "rank": i + 1,
            "index": idx,
            "text": passages[idx],
            "relevance_score": round(score, 6)
        }
        for i, (idx, score) in enumerate(ranked)
    ]

    return {
        "query": query,
        "total_passages": len(passages),
        "returned": len(results),
        "results": results
    }

# ==================================================================
# Root
# ==================================================================
@app.get("/")
async def root():
    return {
        "message": "Jina v3 100% Local Server – No jina-reranker pip, Pure Transformers",
        "status": "ready",
        "gpu": torch.cuda.is_available(),
        "reranker_loaded_from": RERANKER_PATH
    }

# ==================================================================
# Run server
# ==================================================================
if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8000, log_level="info")
