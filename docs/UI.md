# Helio Console: UI architecture

An operator and reader console whose job is transparency: every answer is shown with its citations, retrieval trace, latency and cost. Built as native web components with no framework and no build step, so a .NET team can own it without a JavaScript toolchain.

## Component architecture

```
index.html
├── <helio-status>       readiness badge, API key entry            (topbar)
├── <helio-ask>          question form, suggestions, cancel        (main column)
├── <helio-answer>       answer, citations, trace, retrieval table (main column)
├── <helio-documents>    knowledge base listing                     (side column)
├── <helio-ingest>       operator ingest form                       (side column)
└── <helio-queries>      recent query records                       (side column)
            all HTTP through components/api.js (auth header, error shaping, escaping helpers)
```

Components use light DOM so the single stylesheet (`styles.css`, design tokens on `:root`, light and dark themes) styles all of them, and so assistive technology sees one document tree.

### Communication (events, not shared state)

| Event | Dispatched by | Consumed by | Detail |
|---|---|---|---|
| `helio:asking` | helio-ask | helio-answer | `{ question }` |
| `helio:answered` | helio-ask | helio-answer, helio-queries | `{ response, requestId }` |
| `helio:failed` | helio-ask | helio-answer | `{ error }` or `{ cancelled: true }` |
| `helio:ingested` | helio-ingest (on `document`) | helio-documents | ingest result |
| `helio:auth-changed` | api.js (on `window`) | status, documents, queries | none |

## Props and API

| Component | Attributes | Public behaviour |
|---|---|---|
| `helio-ask` | `max-length` (default 1000) | `submit(text)`; Ctrl+Enter submits; cancel aborts the in-flight request |
| `helio-answer` | none | renders from events; `renderAnswer(response)` is callable for embedding elsewhere |
| `helio-queries` | `take` (default 8) | `load()` |
| `helio-documents` | none | `load()` |
| `helio-ingest` | none | `submit()` |
| `helio-status` | none | `check()`, `promptKey()` |

`api.js` exports `api.ask/ingest/documents/queries/ready`, `ApiError { status, title, errors }`, `getApiKey/setApiKey` (session storage only), and `esc()` which every component uses on every string that came from the server.

## States handled

| Component | Loading | Empty | Error | Edge cases |
|---|---|---|---|---|
| ask | button disabled, status text, cancel visible | validation message on empty input | field-level error from `errors.question`; network error surfaced in answer panel | overlong input blocked by `maxlength` and by the server; duplicate submits abort the previous request |
| answer | three-line skeleton, `aria-busy` | prompt to ask | `role=alert`, 401 hint to set a key | abstention rendered as a warning with reason; cached answers badged; zero citations handled |
| documents | skeleton | "No documents yet" | 401 hint | long titles wrap; dates localised |
| ingest | button disabled, progress text | n/a | field-level errors mapped from problem details (`sourceId` → source field) | unchanged content reported distinctly; form reset only on success |
| queries | skeleton | "No queries yet" | reader key explained, no request made without a key | question truncated with full text in `title` |
| status | "checking" | n/a | "service unavailable" badge | polls every 30 s; key stored for the session only |

## Accessibility

- Every input has a `<label>`; errors are `role="alert"` and linked by `aria-describedby`; invalid fields set `aria-invalid`.
- Live regions: answer panel and status badge use `aria-live`; loading states set `aria-busy`.
- Skip link, visible focus ring on every interactive element, logical heading order, semantic tables for the retrieval trace.
- Colour is never the only signal (badges carry text); contrast checked in both themes; `prefers-reduced-motion` disables the skeleton shimmer.
- Verified in headless Chromium: no console errors, no unlabeled inputs, no horizontal overflow at 390 px.

## Responsive design

Two-column grid above 900 px, single column below; topbar subtitle hidden below 600 px; stat tiles reflow with `auto-fit`.

## Usage example

Embedding the answer view on another page:

```html
<link rel="stylesheet" href="/styles.css">
<helio-ask max-length="500"></helio-ask>
<helio-answer></helio-answer>
<script type="module" src="/components/api.js"></script>
<script type="module" src="/components/helio-ask.js"></script>
<script type="module" src="/components/helio-answer.js"></script>
```

## Best practices followed

- One HTTP client, one place for auth and error shaping.
- Escape at the render boundary, always, with `esc()`.
- Abort controllers for cancellable requests; no dangling fetches after navigation.
- No global mutable state; events carry data.
- Keys in `sessionStorage` only, never `localStorage`, never in URLs.
- CSP from the server forbids inline scripts; all scripts are module files.
