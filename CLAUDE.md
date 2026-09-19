# CLAUDE.md

Arabic-first RAG service: ASP.NET Core 8 API (`AiService/`) that extracts text from
uploaded documents with a Python script, chunks it, embeds it, and stores it in Qdrant.

## Working rules for this project

- **Measure before recommending.** Several plausible-sounding claims in past sessions
  were wrong until tested against the real files in `AiService/Uploads/` (see
  "Things that turned out to be wrong"). Run the thing, then conclude.
- Arabic correctness beats speed. A fast extractor that transposes ligatures is worse
  than a slow correct one.
- When editing Python or C# through a bash heredoc, backslashes get collapsed
  (`\\n` becomes a real newline, `"\\|"` becomes `"\|"`). Write patch scripts with the
  Write tool, or build backslashes with `chr(92)`.

## Pipeline

```
POST /api/FileUpload/upload                      Controllers/FileUploadController.cs
  -> EmbeddingService.IndexUploadedFilesAsync     Services/EmbeddingService.cs
       enumerate Uploads/** (IsIndexableInput: allowlist, skips Extracted/, .ocr_cache)
       skip files already in Qdrant (by file_name)
       extract -> Extractor.ExtractAllText / ExtractWithYourCurrentExtractPy_Claude
                    Models/Extractor.cs -> runs C:\Python313\python.exe extract_universal.py
                    reads Uploads/Extracted/<stem>/<stem>_clean.txt
       ArabicStuckWordsFixer.FixStuckWords        Services/ArabicStuckWordsFixer.cs
       chunk:
         Extraction:UsePythonChunks=false -> _semantic.SemanticChunkAsync   (current default)
         Extraction:UsePythonChunks=true  -> LoadPythonChunks(<stem>_chunks.json)
       embed (multilingual-e5-large-instruct, 512-token limit) -> Qdrant
```

Payload written to Qdrant: `Text, PageNumber, file_name, ChunkType, upload_date, source,
source_type`. Retrieval only reads `Text` and `file_name` back. `Section` is NOT in the
payload, which is why the chunker puts the section into `Text` itself.

## Voice (STT / TTS)

UI flow: voice button records -> `POST /api/Chat/Transcribe` (multipart `file`) -> `{text}` into
chat box -> `POST /api/Chat/ChatWithRAG` -> optional `SpeakPrepare` for audio.

- **Active STT since 2026-09-18: `stt_server_audar.py` (port 8013)**. `Stt:Active` in appsettings
  (`Audar` | `Whisper`) picks an entry under `Stt:Models` (Script, BaseUrl, ModelDir); restart the
  API to switch. `PythonServerLauncher.Setting()` resolves `Models:<Active>:<key>`, then the flat
  `Stt:<key>` (Tts has no Active and uses flat keys). Which one runs: GET :8013/health or :8011/health,
  or the `Started STT server <script>` log line. Both verified through the API. Model `C:\Work\PROJECTS\audaraiAudar_ASR_V1_Flash`: Audar-ASR-V1-Flash GGUF
  (0.6B Qwen3 decoder + `mmproj` audio encoder, projector `qwen3a`). No torch/transformers: the
  script starts `llama-server.exe` from `C:\Work\PROJECTS\llama.cpp-b11042` (official CPU build)
  on internal port 8014, and restarts it if it dies (on /transcribe or /health). Output `language Arabic<asr_text>...` is stripped.
  Measured on the same 4 TTS clips: ready in 3-5 s, **2.3 GB RAM**, 0.65-0.95 s per clip (Whisper
  6-9 s), 45 s audio in 8 s. Errors: `كم الحد`->`كما الحب`, `الترخيص`->`الترفيهي` (short clips;
  the same sentences came out right inside longer audio). Q4_K_M is ~2x faster, 0.5 GB less, and
  worse (`الأضمن`, `رصوم`), so BF16 is the default (`STT_GGUF` to change). Windows of 26-29 s
  skipped whole sentences, 20 s did not, so audio is cut at 20 s on the quietest 100 ms.
  Noise -> `Thank you.` (hallucination, not filtered); `(Laughter)` tags are stripped.
  Verified through the API: wav, webm/opus, auto-start, restart after kill.
- `stt_server.py` (port 8011, previous STT, code kept): Whisper fine-tune
  `C:\Work\PROJECTS\WASFYSPEECH\whisper-large-v3-turbo-arabic-dialectal` (809M, card WER 0.344).
  Replaced wav2vec2 `arabic_speech_to_text` (folder deleted) on 2026-09-14. Persistent server;
  PyAV decodes webm/opus (no ffmpeg.exe). Max 120 s; >30 s uses Whisper long-form with
  timestamps. Output cleaned like the model's `normalize.py` (no tashkil/tags) + `ی`->`ي`.
  Log: `stt_server.log`. `STT_DTYPE=bfloat16` env is supported but unmeasured.
- The model was saved by transformers 5.13; this machine has 4.57.3 (shared with Surya OCR and
  TTS, so not upgraded). Two load-time workarounds in `stt_server.py`: `extra_special_tokens={}`
  (list-vs-dict crash) and `eos_token_id` list -> int (long-form "slice indices" crash).
- Measured (CPU, with the old STT server still holding 2.7 GB): load 12 s, **3.6 GB RAM**,
  2–5 s clips take 6–9 s, 35 s of 10 sentences took 18.6 s and came back complete.
  Clearly better than wav2vec2 (`تأسيس`, `الكويت`, `استخرج` now right) but still adds
  endings: `الواحد`->`الواحدة؟`, `الترخيص`->`ترخيصة`, `المال`->`المالي`. Adds `؟`.
- Whisper hallucinates on noise: 3 s white noise -> `إيه ده!`. The RMS gate only catches
  silence. `<|nospeech|>` prob is 0.000 for speech and noise alike (fine-tune lost it), so it
  cannot be used as a filter. 9 identical repeated sentences collapsed to 3 (Whisper habit).
- `tts_server.py` (port 8010, `Tts` section): Nabra-7M-Distill.
- Both launched by `Services/PythonServerLauncher.cs` (base class; `TtsServerLauncher`,
  `SttServerLauncher`), started with the API and restarted on demand by `SttService`/`TtsService`.
- All accuracy numbers use TTS-generated speech; not yet tested on real microphone recordings.
- Endpoint verified through the API (with the wav2vec2 model; the Whisper server was tested
  directly on port 8012, not through the API yet): wav and `audio/webm;codecs=opus` -> 200; bad data 400;
  empty 400; silence 422 (RMS gate + <2-letter results dropped, CTC emitted `ة` on silence);
  125 s -> 413; killed STT server restarted on the next request (~16 s). Fixed on the way:
  `MediaTypeHeaderValue` ctor threw on `;codecs=opus` (500), and the launcher never closed its
  log writer, so every restart failed with "file in use" (TTS had the same bug).

## Key files

| File | Role |
|---|---|
| `AiService/extract_universal.py` | The extractor. Replaced `extract_claude_up.py` (kept, unused). |
| `AiService/compare_extractors.py` | A/B harness: old vs new extractor over a folder. |
| `AiService/Models/Extractor.cs` | Runs the Python script. `scriptPath` resolved relative to the project, absolute path as fallback. |
| `AiService/Services/EmbeddingService.cs` | Indexing, format routing, Stage B switch, Qdrant upsert. |
| `AiService/Models/ImprovedSemanticChunker.cs` | `SemanticChunkAsync`, `ChunkingOptions_NEW`. |
| `AiService/Services/ArabicStuckWordsFixer.cs` | Join/hamza dictionary applied before chunking. |
| `AiService/appsettings.json` | `Extraction:UsePythonChunks` flag. |

Collection name is a `const UNIFIED_COLLECTION` in `EmbeddingService.cs` (currently
`sem_New_Test`), switched by hand via the commented stack above it.

## extract_universal.py

CLI (same contract as the old script): `python extract_universal.py <input> <output_dir>`.
Writes `<stem>_clean.txt` (what C# reads), plus `_chunks.json`, `_sections.json`,
`_pages.json`, `_tables.json/.md/_csv/`, `_full.json`, `_stats.json`.
`_chunks.jsonl` only with `--jsonl` (the C# enumerator used to pick it up and crash).

Formats: pdf, docx, xlsx/xls, pptx, html, txt/csv/tsv/json/md/rtf, png/jpg/tif/bmp/webp.

Useful flags: `--no-ocr`, `--no-ocr-embedded`, `--ocr-on-lossy-text`, `--verify-lossy`,
`--no-tables`, `--max-tokens`, `--skip-existing`, `-v`.

### Design decisions and the evidence behind them

- **PDF text backend is scored per document, pypdf usually wins.** PyMuPDF and pdfium
  transpose lam-alef ligatures in these fonts (`الم`->`امل`, `في`->`يف`). On the 48-page
  handbook: PyMuPDF 1136 corrupt / 100 healthy sequences, pypdf 22 / 1131. pdfplumber
  returns fully reversed (visual-order) Arabic. Marker and pymupdf4llm build on PyMuPDF,
  so they inherit the damage.
- **Lossy CMap fonts.** Some PDFs omit lam-alef / yaa-nun glyphs from their ToUnicode map,
  so every extractor drops them (`الصلاة`->`الصاة`, `العالمين`->`العالمن`). Detected by
  frequency of `لا` and word-final `ين`; logged as `TEXT LAYER IS LOSSY`. Only OCR
  (`--ocr-on-lossy-text`) recovers it.
- **OCR is Surya**, lazy-loaded, cached by image hash in `<output>/.ocr_cache`. Tesseract
  on this machine is v3.02 with no Arabic data, so it is unusable. Torch is CPU-only:
  roughly 10 s per page, ~36 s model load. OCR runs only on pages with no usable text and
  on sizeable embedded images, never forced on every page.
- **Scanned PDFs with a hidden OCR layer** (full-page image >=85% + all text render mode 3,
  `_scanned_page_xref`): Surya OCR of the scan REPLACES the hidden layer, and those pages
  are excluded from the lossy-font check. Before, the page came out twice (scanner's garbage
  OCR + appended embedded-image OCR) and falsely flagged lossy. `minsitry_Trial.pdf`: 19 -> 10
  chunks. Verified old vs new `--no-ocr` on all uploads: no other text/chunks/tables changed.
- **Persian `ی`/`ک` -> Arabic `ي`/`ك`** for documents detected arabic/mixed (OCR and some fonts
  emit them; they never match at search time). Changed `ldlyl_lrshdy_bshn_hq_ltl.pdf` (4 lines).
- **Tables: geometry from PyMuPDF `find_tables()`, text from word boxes sorted RTL.**
  `find_tables()` detects structure well but returns Arabic cell text character-reversed.
  Ligature repair on cells is gated on PyMuPDF's damage score (cells always come from
  PyMuPDF), not the winning backend's.
- **Table rows become their own chunks** with headers inlined
  (`نقل ملكية البراءة — الرسم للشركات: 70، الرسم للأفراد: 35`) so a lookup question
  matches one tight vector.
- Sections are written back into `_clean.txt` as `#` headings so the C# chunker can see
  them.
- No heuristic word-splitting. The original `aggressive_word_separation` regex manufactured
  `المعلومات`->`المعل ومات` — the very damage `STUCK_WORDS` then tried to repair.

## Chunker (ImprovedSemanticChunker)

Current `ChunkingOptions_NEW` changes and why:

- `MaxTokensPerChunk = 400` — token budget checked before line limits. Real chunker
  before: 10/135 chunks over 512 tokens (max 664). After: 1/143 (max 535).
- `IncludeContextHeader = true` — prepends `section › subsection` to `Text`.
  Chunks with context went from 50/135 to 132/143.
- Overlap rewritten: verbatim tail slice to a sentence (incl. `؟` `؛`) or word boundary.
  The old one rewrote text and, in the MAX_LINES / FORCE_BREAK paths, was appended to a
  copy of the buffer and discarded, so those chunks had no overlap at all.
- `DetectSectionWithLevel` checks `#` headings before its `<=12 char` guard (it was
  discarding `# الفهرس`).
- `MinSemanticCheckLines = 12`, `SemanticCheckInterval = 8` (were 50/40, which could never
  fire inside a 400-token chunk). First check lands at 16 lines.
- `GetEmbeddingsBatchAsync` returns zero vectors on failure instead of throwing. Zero
  vectors gave similarity 0, so a dead server reported a semantic break at every check.
  Now detected (`IsDegenerate`), plus a 10 s HttpClient timeout and a circuit breaker that
  disables semantic checks after 3 consecutive failures.

## Changes to C# by stage

- **Stage 1** — `Extractor.cs` points at `extract_universal.py`.
- **Stage A** — all formats route to the Python extractor; switch has a default arm
  (was `SwitchExpressionException`); file enumeration is an allowlist. The old C#
  `ExtractDocxText` ran Docnet (PDFium) on .docx and always returned "".
- **Stage B** — `Extraction:UsePythonChunks` flag, `LoadPythonChunks` maps
  `_chunks.json` to `Chunk_NEW`, falls back to the C# chunker if the file is missing.
  Mapping verified against real output (364 chunks, no empty fields). **Off by default.**
- **Stage C** — `ArabicStuckWordsFixer` KEPT. Measured 377 useful hits on the corpus (mostly
  hamza restoration: `او`->`أو`). Removed harmful entries: `فيما`->`في ما` (fired 7x),
  `الالية`->`التالية`, `الاليتين`->`التاليتين`, and two that inserted diacritics.

## Things that turned out to be wrong (don't repeat them)

- "Retire ArabicStuckWordsFixer" — it does real work; see Stage C.
- "Replace the per-entry regex loop with one compiled alternation" — measured 2.4x slower
  at every size up to 5.4 MB. The loop stays; a comment records the numbers.
- "20 of 21 chunks exceed 512 tokens" — that measured naive 120-line windows, not the real
  chunker. Real figure was 7.4%.
- "Table data is unretrievable" — true only of the original pipeline; `_clean.txt` has
  included tables since stage 1.

## Open items / not yet verified

- **End-to-end indexing never ran.** Qdrant (localhost:6333), the embedding server
  (10.21.2.143:8000) and Ollama (11434) were all down; Docker Desktop's engine was not
  running. Start Qdrant with `docker compose up -d qdrant` from the repo root.
- `UsePythonChunks` true vs false has not been compared on real queries. Plan: index into
  a separate collection with the flag on, ask both the same ~10 questions.
- Semantic thresholds 12/8 are safe but their effect on boundaries is unmeasured.
- OCR'd tables (scanned pages) come out column-major, so they don't become row chunks.
- Residual ligature errors in PyMuPDF table cells (e.g. `العالمة` for `العلامة`).
- Not supported: legacy `.doc`, `.msg`, `.odt`, `.epub`. RTF is a crude regex strip.
  `.xls` path is untested. No parallelism.

## Build and run

```
cd AiService
dotnet build AiService.csproj          # stop a running AiService.exe first; it locks the exe
dotnet run --project AiService.csproj  # http://localhost:5122, Swagger at /swagger
python extract_universal.py Uploads out_dir --no-ocr     # fast extraction check
python compare_extractors.py Uploads compare_out --no-ocr
```

Python interpreter used by the app: `C:\Python313\python.exe`.
