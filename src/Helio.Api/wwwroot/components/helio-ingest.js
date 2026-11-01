import { api, ApiError, esc } from './api.js';

/**
 * <helio-ingest>: operator form to add or update a document.
 * Events: dispatches 'helio:ingested' on document after success.
 * States: idle, submitting, success (with version and chunk count), unchanged, error (field-level where available).
 */
class HelioIngest extends HTMLElement {
  connectedCallback() {
    this.innerHTML = `
      <form class="card" novalidate>
        <h2>Ingest a document <span class="muted small">operator</span></h2>
        <div class="field">
          <label for="ingest-source">Source id</label>
          <input id="ingest-source" name="sourceId" maxlength="200" required placeholder="e.g. battery-params" aria-describedby="ingest-source-error">
          <div class="hint">Stable identifier. Re-ingesting the same id creates a new version; unchanged text is skipped.</div>
          <div class="error" id="ingest-source-error" role="alert" hidden></div>
        </div>
        <div class="field" style="margin-top:10px">
          <label for="ingest-title">Title</label>
          <input id="ingest-title" name="title" maxlength="300" required aria-describedby="ingest-title-error">
          <div class="error" id="ingest-title-error" role="alert" hidden></div>
        </div>
        <div class="field" style="margin-top:10px">
          <label for="ingest-text">Text</label>
          <textarea id="ingest-text" name="text" rows="6" required placeholder="Paste markdown or plain text. Blank lines separate paragraphs." aria-describedby="ingest-text-error"></textarea>
          <div class="error" id="ingest-text-error" role="alert" hidden></div>
        </div>
        <div style="display:flex;gap:10px;align-items:center;margin-top:12px;flex-wrap:wrap">
          <button type="submit" class="btn btn-primary">Ingest</button>
          <span class="muted small" data-result aria-live="polite"></span>
        </div>
      </form>`;

    this.querySelector('form').addEventListener('submit', e => { e.preventDefault(); this.submit(); });
  }

  setFieldError(field, message) {
    const el = this.querySelector(`#ingest-${field}-error`);
    if (!el) return;
    el.textContent = message || '';
    el.hidden = !message;
    const input = this.querySelector(`#ingest-${field}`);
    if (input) input.setAttribute('aria-invalid', message ? 'true' : 'false');
  }

  async submit() {
    const form = this.querySelector('form');
    const data = Object.fromEntries(new FormData(form).entries());
    ['source', 'title', 'text'].forEach(f => this.setFieldError(f, ''));
    const result = this.querySelector('[data-result]');
    const button = this.querySelector('button[type="submit"]');

    let invalid = false;
    if (!data.sourceId.trim()) { this.setFieldError('source', 'Source id is required.'); invalid = true; }
    if (!data.title.trim()) { this.setFieldError('title', 'Title is required.'); invalid = true; }
    if (!data.text.trim()) { this.setFieldError('text', 'Text is required.'); invalid = true; }
    if (invalid) return;

    button.disabled = true;
    result.textContent = 'Chunking, embedding and indexing…';
    try {
      const { data: res } = await api.ingest({ sourceId: data.sourceId.trim(), title: data.title.trim(), text: data.text });
      result.innerHTML = res.unchanged
        ? `<span class="badge">unchanged</span> already indexed as v${res.version}`
        : `<span class="badge badge-ok">indexed</span> v${res.version}, ${res.chunkCount} chunk${res.chunkCount === 1 ? '' : 's'}`;
      document.dispatchEvent(new CustomEvent('helio:ingested', { detail: res }));
      if (!res.unchanged) form.reset();
    } catch (error) {
      if (error instanceof ApiError && Object.keys(error.errors).length) {
        for (const [field, messages] of Object.entries(error.errors)) {
          this.setFieldError(field === 'sourceId' ? 'source' : field, messages.join(' '));
        }
        result.textContent = '';
      } else {
        result.innerHTML = `<span class="badge badge-bad">failed</span> ${esc(error.message)}`;
      }
    } finally {
      button.disabled = false;
    }
  }
}

customElements.define('helio-ingest', HelioIngest);
