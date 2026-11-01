import { api, getApiKey, setApiKey, esc } from './api.js';

/**
 * <helio-status>: readiness indicator and API key entry.
 * Attributes: none. Events: dispatches 'helio:auth-changed' (via api.js) when the key changes.
 * States: checking, ready (with chunk count), unavailable.
 */
class HelioStatus extends HTMLElement {
  connectedCallback() {
    this.render('checking');
    this.check();
    this._timer = setInterval(() => this.check(), 30_000);
    this._onAuth = () => this.render(this._state, this._detail);
    window.addEventListener('helio:auth-changed', this._onAuth);
  }

  disconnectedCallback() {
    clearInterval(this._timer);
    window.removeEventListener('helio:auth-changed', this._onAuth);
  }

  async check() {
    try {
      const { data } = await api.ready();
      if ((data?.detail ?? '') !== 'Healthy') throw new Error('not ready');
      // The readiness endpoint deliberately says only "Healthy"; the chunk count comes from the (authenticated) document list.
      let label = 'ready';
      if (getApiKey()) {
        try {
          const docs = (await api.documents()).data ?? [];
          const chunks = docs.reduce((sum, d) => sum + (d.chunkCount ?? 0), 0);
          label = `${chunks} chunks indexed`;
        } catch { /* a reader without a key still sees "ready" */ }
      }
      this.render('ready', label);
    } catch {
      this.render('unavailable', 'service unavailable');
    }
  }

  render(state, detail = '') {
    this._state = state; this._detail = detail;
    const badge = state === 'ready' ? 'badge-ok' : state === 'unavailable' ? 'badge-bad' : '';
    const label = state === 'checking' ? 'checking' : detail;
    const hasKey = Boolean(getApiKey());
    this.innerHTML = `
      <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
        <span class="badge ${badge}" role="status">${esc(label)}</span>
        <button type="button" class="btn btn-ghost small" data-action="key" aria-haspopup="dialog">${hasKey ? 'Change API key' : 'Set API key'}</button>
      </div>`;
    this.querySelector('[data-action="key"]').addEventListener('click', () => this.promptKey());
  }

  promptKey() {
    const next = window.prompt('Paste your Helio API key (stored for this browser session only). Leave empty to clear.', getApiKey());
    if (next === null) return;
    setApiKey(next.trim());
    this.render(this._state, this._detail);
  }
}

customElements.define('helio-status', HelioStatus);
