import { api, ApiError } from './api.js';

const SUGGESTIONS = [
  'What bulk charge voltage should I set for a 16S lithium iron phosphate bank?',
  'What does fault code F07 mean on a hybrid inverter?',
  'Why is voltage a bad way to estimate state of charge?',
  'What extinguisher should be used on a lithium battery fire?',
];

/**
 * <helio-ask>: the question form.
 * Attributes: max-length (default 1000).
 * Events: 'helio:asking' { question }, 'helio:answered' { response }, 'helio:failed' { error }.
 * States: idle, submitting (button disabled, request cancellable), error (inline, field-level when the API returns one).
 */
class HelioAsk extends HTMLElement {
  static get observedAttributes() { return ['max-length']; }

  connectedCallback() {
    this.maxLength = Number(this.getAttribute('max-length') || 1000);
    this.render();
  }

  render() {
    this.innerHTML = `
      <form class="card" novalidate>
        <h2>Ask a question <span class="muted small">answers cite the passages they come from</span></h2>
        <div class="field">
          <label for="helio-question">Question</label>
          <textarea id="helio-question" name="question" maxlength="${this.maxLength}" rows="3" required
            placeholder="For example: what float voltage should a 48 V lithium bank use?" aria-describedby="helio-question-hint helio-question-error"></textarea>
          <div class="hint" id="helio-question-hint"><span data-count>0</span>/${this.maxLength} characters. Press Ctrl+Enter to ask.</div>
          <div class="error" id="helio-question-error" role="alert" hidden></div>
        </div>
        <div style="display:flex;gap:10px;align-items:center;margin-top:12px;flex-wrap:wrap">
          <button type="submit" class="btn btn-primary">Ask</button>
          <button type="button" class="btn btn-ghost" data-action="cancel" hidden>Cancel</button>
          <span class="muted small" data-status aria-live="polite"></span>
        </div>
        <div class="suggestions" aria-label="Example questions">
          ${SUGGESTIONS.map(s => `<button type="button" class="chip">${s}</button>`).join('')}
        </div>
      </form>`;

    const form = this.querySelector('form');
    const textarea = this.querySelector('textarea');
    const count = this.querySelector('[data-count]');

    textarea.addEventListener('input', () => { count.textContent = String(textarea.value.length); this.setError(''); });
    textarea.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); form.requestSubmit(); } });
    form.addEventListener('submit', e => { e.preventDefault(); this.submit(textarea.value); });
    this.querySelector('[data-action="cancel"]').addEventListener('click', () => this._controller?.abort());
    this.querySelectorAll('.chip').forEach(chip => chip.addEventListener('click', () => { textarea.value = chip.textContent; count.textContent = String(textarea.value.length); form.requestSubmit(); }));
  }

  setBusy(busy) {
    this.querySelector('button[type="submit"]').disabled = busy;
    this.querySelector('[data-action="cancel"]').hidden = !busy;
    this.querySelector('[data-status]').textContent = busy ? 'Retrieving and generating…' : '';
  }

  setError(message) {
    const el = this.querySelector('#helio-question-error');
    el.textContent = message;
    el.hidden = !message;
    this.querySelector('textarea').setAttribute('aria-invalid', message ? 'true' : 'false');
  }

  async submit(raw) {
    const question = raw.trim();
    if (!question) { this.setError('Type a question first.'); this.querySelector('textarea').focus(); return; }

    this._controller?.abort();
    this._controller = new AbortController();
    this.setBusy(true);
    this.setError('');
    this.dispatchEvent(new CustomEvent('helio:asking', { bubbles: true, detail: { question } }));

    try {
      const { data, headers } = await api.ask(question, this._controller.signal);
      this.dispatchEvent(new CustomEvent('helio:answered', { bubbles: true, detail: { response: data, requestId: headers.get('X-Helio-Request-Id') } }));
    } catch (error) {
      if (error.name === 'AbortError') {
        this.dispatchEvent(new CustomEvent('helio:failed', { bubbles: true, detail: { error: null, cancelled: true } }));
      } else {
        if (error instanceof ApiError && error.errors.question) this.setError(error.errors.question.join(' '));
        this.dispatchEvent(new CustomEvent('helio:failed', { bubbles: true, detail: { error } }));
      }
    } finally {
      this.setBusy(false);
    }
  }
}

customElements.define('helio-ask', HelioAsk);
