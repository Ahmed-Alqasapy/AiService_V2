# Text-to-Speech API — UI integration guide

How the UI plays a chat answer out loud. The API turns text into Arabic speech with the
**Nabra-7M-Distill** model, served from `AiService`.

## Flow

```
User sends question
   │
   ▼
POST /api/Chat/ChatWithRAG        ──►  { "message": "..." }   ──► show text on screen immediately
   │
   ▼  (don't wait for audio before showing the text)
POST /api/Chat/SpeakPrepare       ──►  { "id": "...", "audioUrl": "/api/Chat/SpeakAudio/<id>" }
   │
   ▼
<audio src = API_BASE + audioUrl>  ──►  GET /api/Chat/SpeakAudio/<id>  ──► audio/wav, play()
```

**Use `SpeakPrepare` + `SpeakAudio` from the browser, not `POST /api/Chat/Speak`.**
On the development machine, Kaspersky web protection drops audio returned from a browser
`POST`: the browser receives an empty `204` and the request never reaches the API. Audio
fetched with `GET` passes. The two-step flow uses a `POST` that returns only JSON, then a
`GET` for the audio. It also works with long replies, which would not fit in a GET query string.

---

## Base URL

| Run mode | Base URL |
|---|---|
| Visual Studio (IIS Express) | `https://localhost:44356` |
| `dotnet run` (Kestrel) | `http://localhost:5122` |

CORS currently allows only `http://localhost:22823` (`Program.cs`, policy `AllowLocalhost`).
If the UI runs on a different origin, add it there.

---

## Endpoints

### 1. `POST /api/Chat/SpeakPrepare`

Generates the speech and keeps it on the server for **10 minutes**.

Request:
```json
{
  "text": "مرحبًا بكم! يمكنكم الاستفسار عن التراخيص.",
  "speed": 1.0
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `text` | string | yes | The chat answer. Markdown symbols (`*`, `#`, `` ` ``, links) are stripped server-side. Max ~4000 characters are spoken. |
| `speed` | number | no | `0.5`–`2.0`, default `1.0`. |

Response `200`:
```json
{
  "id": "3f2c9a0d8b1e4c6f9a7d2e5b1c0f8a6e",
  "audioUrl": "/api/Chat/SpeakAudio/3f2c9a0d8b1e4c6f9a7d2e5b1c0f8a6e"
}
```

Errors:

| Status | Body | Meaning / UI action |
|---|---|---|
| `400` | `{ "error": "Text cannot be empty." }` | Don't call with empty text. |
| `503` | `{ "error": "Text-to-speech service is unavailable." }` | TTS model still loading (first ~20–40 s after API start) or crashed. Keep the text, hide/disable the speaker button, optionally retry later. |

`audioUrl` is relative. Prefix it with the API base URL.

### 2. `GET /api/Chat/SpeakAudio/{id}`

Returns the WAV file (`audio/wav`, 24 kHz, 16-bit mono). Supports range requests, so
seeking in `<audio>` works. Use it directly as an audio `src`.

| Status | Meaning |
|---|---|
| `200` | Audio |
| `404` | `{ "error": "Audio not found or expired." }` More than 10 minutes old, or the API restarted. Call `SpeakPrepare` again. |

### Other TTS endpoints (not for the browser UI)

| Endpoint | Use |
|---|---|
| `POST /api/Chat/Speak` `{ text, speed }` → `audio/wav` | Server-to-server / curl. Blocked by Kaspersky in the browser (see above). |
| `GET /api/Chat/Speak?text=...&speed=...` → `audio/wav` | Quick manual test in a browser tab. The URL length limit caps the text. |

---

## Implementation

### Plain JavaScript

```js
const API_BASE = 'https://localhost:44356';

const player = new Audio();

async function speak(text) {
  if (!text?.trim()) return;

  const res = await fetch(`${API_BASE}/api/Chat/SpeakPrepare`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text, speed: 1.0 }),
  });
  if (!res.ok) {
    console.warn('TTS unavailable', res.status, await res.text());
    return;
  }

  const { audioUrl } = await res.json();
  player.pause();
  player.src = API_BASE + audioUrl;
  try {
    await player.play();
  } catch (err) {
    // NotAllowedError = browser autoplay policy. Show a ▶ button and call player.play() on click.
    console.warn('Autoplay blocked', err);
  }
}

async function sendMessage(question) {
  const res = await fetch(`${API_BASE}/api/Chat/ChatWithRAG`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: question }),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.error);

  renderMessage(data.message);   // 1. show the text first
  speak(data.message);           // 2. then speak it; no await, so the UI stays responsive
}
```

### Angular (service)

```ts
import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class TtsService {
  private readonly apiBase = 'https://localhost:44356';
  private readonly player = new Audio();

  constructor(private http: HttpClient) {}

  async speak(text: string, speed = 1.0): Promise<void> {
    if (!text?.trim()) return;
    try {
      const { audioUrl } = await firstValueFrom(
        this.http.post<{ id: string; audioUrl: string }>(
          `${this.apiBase}/api/Chat/SpeakPrepare`, { text, speed }));
      this.player.pause();
      this.player.src = this.apiBase + audioUrl;
      await this.player.play();
    } catch (err) {
      console.warn('TTS failed or autoplay blocked', err);
    }
  }

  stop(): void {
    this.player.pause();
  }
}
```

Usage after the chat response:
```ts
this.messages.push({ role: 'assistant', text: res.message });
this.tts.speak(res.message);
```

### React (hook)

```tsx
import { useRef, useCallback } from 'react';

const API_BASE = 'https://localhost:44356';

export function useTts() {
  const player = useRef<HTMLAudioElement>(new Audio());

  const speak = useCallback(async (text: string, speed = 1.0) => {
    if (!text?.trim()) return;
    const res = await fetch(`${API_BASE}/api/Chat/SpeakPrepare`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text, speed }),
    });
    if (!res.ok) return;
    const { audioUrl } = await res.json();
    player.current.pause();
    player.current.src = API_BASE + audioUrl;
    player.current.play().catch(err => console.warn('Autoplay blocked', err));
  }, []);

  const stop = useCallback(() => player.current.pause(), []);
  return { speak, stop };
}
```

---

## UX notes

- **Show text first, speak second.** Speech generation for a typical answer takes ~0.3–1 s,
  but the first request after the API starts can take 20–40 s while the model loads.
- **Autoplay policy.** Browsers allow `play()` only after the user has interacted with the
  page (a click or a key press). Sending a chat message counts, so autoplay normally works. If
  `play()` rejects with `NotAllowedError`, show a ▶ button that calls `play()`.
- **One voice at a time.** Reuse a single `Audio` element and `pause()` it before a new
  answer, so replies don't overlap.
- **Mute toggle.** Store a user preference such as `ttsEnabled` and skip `speak()` when it is off.
- **Replay.** Keep `audioUrl` on the message object to replay within 10 minutes. After
  that, call `SpeakPrepare` again with the same text.
- **Failure is non-fatal.** If TTS returns `503`, the chat still works. Don't show an error
  dialog; just skip the audio.

## Language limits

- Modern Standard Arabic, single voice.
- Latin-script words (English names, codes) are **dropped** from the speech, not spoken.
- Tashkeel is added automatically (camel-tools). Text that already has diacritics is used as-is
  and is pronounced more accurately.

## Backend reference

| Piece | Location |
|---|---|
| Endpoints | `AiService/Controllers/ChatController.cs` (`SpeakPrepare`, `SpeakAudio`, `Speak`) |
| HTTP client to the model | `AiService/Services/TtsService.cs` |
| Starts/stops the Python server | `AiService/Services/TtsServerLauncher.cs` |
| Model server (FastAPI, port 8010) | `AiService/tts_server.py`, log in `AiService/tts_server.log` |
| Config | `AiService/appsettings.json` → `Tts:BaseUrl`, `AutoStart`, `PythonPath`, `ModelDir` |
| Model files | `C:\Work\PROJECTS\Nabra\Nabra-7M-Distill` |
| Swagger demo | Running `ChatWithRAG` in Swagger auto-plays the answer (`SwaggerTtsAutoplayJs` in `Program.cs`) |
