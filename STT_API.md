# Speech-to-Text API — UI integration guide

How the chat voice button turns the user's speech into a question. The API transcribes
Arabic audio with the **arabic_speech_to_text** model (wav2vec2-xls-r-300m), served from
`AiService`. Companion to `TTS_API.md` (reading answers out loud).

## Flow

```
User taps mic button ──► record with MediaRecorder ──► user taps stop
   │
   ▼
POST /api/Chat/Transcribe  (multipart, field "file")  ──►  { "text": "..." }
   │
   ▼
Put text in the chat input  (let the user fix it — see "Accuracy")
   │
   ▼
POST /api/Chat/ChatWithRAG  { "text": "..." }  ──►  { "message": "..." }   (unchanged)
   │
   ▼  optional
SpeakPrepare / SpeakAudio   (see TTS_API.md)
```

---

## Base URL

| Run mode | Base URL |
|---|---|
| Visual Studio (IIS Express) | `https://localhost:44356` |
| `dotnet run` (Kestrel) | `http://localhost:5122` |

CORS currently allows only `http://localhost:22823` (`Program.cs`, policy `AllowLocalhost`).
If the UI runs on a different origin, add it there.

Browsers only allow microphone access on `https://` pages or `http://localhost`.

---

## Endpoint

### `POST /api/Chat/Transcribe`

Request: `multipart/form-data` with one field.

| Field | Type | Required | Notes |
|---|---|---|---|
| `file` | file | yes | The recording. The field **must** be named `file`. |

Accepted formats: whatever the browser records — `audio/webm;codecs=opus` (Chrome, Edge,
Firefox), `audio/ogg`, `audio/mp4` (Safari) — plus `wav`, `mp3`, `m4a`. The server detects
the format from the bytes, so the file name and content type don't matter.

Limits:

| Limit | Value |
|---|---|
| Max duration | 120 s |
| Max upload size | 15 MB |

Response `200`:
```json
{
  "text": "كم رسوم اصدار الترخيص",
  "duration": 2.2,
  "seconds": 0.63
}
```

| Field | Meaning |
|---|---|
| `text` | Transcribed Arabic text, no punctuation or diacritics. |
| `duration` | Length of the recording in seconds. |
| `seconds` | Time the server spent transcribing. |

Errors (all JSON with an `error` field unless noted):

| Status | Body | Meaning / UI action |
|---|---|---|
| `400` | `{ "error": "Audio file is empty." }` | Recording produced no data (stopped instantly). Ask the user to try again. |
| `400` | `{ "error": "could not decode audio: ..." }` | Not a readable audio file. |
| `400` | validation problem (`errors.file`) | Form field not named `file`. Fix in the UI code. |
| `413` | `{ "error": "audio is 125s; max 120s" }` | Too long. Stop recording automatically at 120 s (see example). An upload over 15 MB may return `413` with no body. |
| `422` | `{ "error": "No speech was recognized.", "duration": 2 }` | Silence or noise. Show "لم يتم التعرف على الكلام، حاول مرة أخرى". Don't send anything to chat. |
| `503` | `{ "error": "Speech-to-text service is unavailable." }` | Model still loading (first ~15–50 s after API start) or crashed. The API restarts it automatically on the next request; show a message and let the user retry. |

---

## Timing

| Situation | Time |
|---|---|
| Normal request, 2–5 s of speech | ~0.5–5 s on CPU |
| 30 s of speech | ~6 s |
| First request after the speech server crashed | ~15 s extra (auto-restart) |

Show a "جارٍ تحويل الصوت إلى نص…" indicator and disable the mic button while waiting.
Use a client timeout of at least 60 s.

---

## Accuracy

The model's published word error rate is **22%**, so expect spelling mistakes.
Measured examples:

| Said | Returned |
|---|---|
| ما هي خطوات تأسيس شركة الشخص الواحد | ما هي خطوات تأسيص الشركة الشخص الواحد |
| كم الحد الأدنى لرأس المال | كم الحظ الأضمى لرأس المالح |
| كيف أستخرج السجل التجاري من بوابة مركز الكويت للأعمال | كيف ستخرج اسجل التجاري من بوابة مركز الكوايت للأعمال |

These were measured with synthetic (TTS) speech; real microphone results are still to be
confirmed.

**Recommendation:** put the text in the chat input and let the user review or edit it, then
send with the normal send button. Don't auto-send to `ChatWithRAG`, because a misheard question
retrieves the wrong documents.

---

## Example (browser JavaScript)

```js
const API_BASE = "http://localhost:5122";
const MAX_SECONDS = 120;

let recorder, chunks = [], stopTimer;

async function startRecording() {
  const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
  // Chrome/Edge/Firefox record webm/opus, Safari records mp4. The server accepts both.
  const mimeType = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4"]
    .find(t => MediaRecorder.isTypeSupported(t));
  recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
  chunks = [];
  recorder.ondataavailable = e => e.data.size && chunks.push(e.data);
  recorder.start();
  stopTimer = setTimeout(stopRecording, MAX_SECONDS * 1000);
}

function stopRecording() {
  return new Promise(resolve => {
    clearTimeout(stopTimer);
    recorder.onstop = () => {
      recorder.stream.getTracks().forEach(t => t.stop()); // release the mic
      resolve(new Blob(chunks, { type: recorder.mimeType }));
    };
    recorder.stop();
  });
}

async function transcribe(blob) {
  const ext = blob.type.includes("mp4") ? "m4a" : "webm";
  const form = new FormData();
  form.append("file", blob, `voice.${ext}`); // field name must be "file"

  const res = await fetch(`${API_BASE}/api/Chat/Transcribe`, { method: "POST", body: form });
  const body = await res.json().catch(() => ({}));

  if (res.ok) return body.text;
  if (res.status === 422) throw new Error("لم يتم التعرف على الكلام، حاول مرة أخرى");
  if (res.status === 503) throw new Error("خدمة تحويل الصوت غير متاحة حالياً، حاول بعد قليل");
  throw new Error(body.error || `HTTP ${res.status}`);
}

// Mic button: tap to start, tap again to stop.
async function onMicClick() {
  if (!recorder || recorder.state === "inactive") return startRecording();
  const blob = await stopRecording();
  try {
    chatInput.value = await transcribe(blob); // user reviews, then presses send
  } catch (e) {
    showError(e.message);
  }
}
```

Don't set the `Content-Type` header yourself when sending `FormData`; the browser adds the
multipart boundary.

---

## Testing without the UI

- Swagger: `http://localhost:5122/swagger` → `POST /api/Chat/Transcribe` → Try it out → choose a file.
- curl:
  ```
  curl -F "file=@question.wav" http://localhost:5122/api/Chat/Transcribe
  ```

---

## Server side (for reference)

| Piece | Location |
|---|---|
| Endpoint | `AiService/Controllers/ChatController.cs` → `Transcribe` |
| Client to Python server | `AiService/Services/SttService.cs` |
| Starts / restarts the Python server | `AiService/Services/PythonServerLauncher.cs` (`SttServerLauncher`) |
| Model server (port 8011) | `AiService/stt_server.py`, log: `AiService/stt_server.log` |
| Settings | `appsettings.json` → `Stt` (`BaseUrl`, `AutoStart`, `PythonPath`, `ModelDir`) |
| Model | `C:\Work\PROJECTS\speechToText\arabic_speech_to_text` |

The Python server starts with the API and keeps the model in memory (loading takes ~48 s),
so requests don't pay the load cost. Persian `ی` in the output is converted to Arabic `ي`
so it matches the indexed documents.
