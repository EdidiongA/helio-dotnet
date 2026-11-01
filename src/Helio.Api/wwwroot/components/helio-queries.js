import { api, esc, formatMs, getApiKey } from './api.js';

/**
 * <helio-queries>: recent query records for operators (what was asked, outcome, latency).
 * Attributes: take (default 8). Refreshes after each answer.
 * States: loading, empty, loaded, forbidden (reader key), error.
 */
class HelioQueries extends HTMLElement {
  connectedCallback() {
    this.take = Number(this.getAttribute('take') || 8);
    this.load();
    this._refresh = () => this.load();
    (this.closest('main') || document).addEventListener('helio:answered', this._refresh);
    window.addEventListener('helio:auth-changed', this._refresh);
  }

  disconnectedCallback() {
    (this.closest('main') || document).removeEventListener('helio:answered', this._refresh);
    window.removeEventListener('helio:auth-changed', this._refresh);
  }

  async load() {
    if (!getApiKey()) {
      this.innerHTML = `<section class="card"><h2>Recent queries</h2><div class="empty">Set an operator API key to view query records.</div></section>`;
      return;
    }
    this.innerHTML = `<section class="card" aria-busy="true"><h2>Recent queries</h2><div class="skeleton"></div></section>`;
    try {
      const { data } = await api.queries(this.take);
      if (!data.length) {
        this.innerHTML = `<section class="card"><h2>Recent queries</h2><div class="empty">No queries recorded yet.</div></section>`;
        return;
      }
      this.innerHTML = `<section class="card"><h2>Recent queries <span class="muted small">operator</span></h2>
        <ul class="list">${data.map(q => `
          <li>
            <span title="${esc(q.question)}">${esc(q.question.length > 60 ? q.question.slice(0, 59) + '…' : q.question)}</span>
            <span class="meta">${q.abstained ? `<span class="badge badge-warn">abstained</span>` : `<span class="badge badge-ok">answered</span>`} ${formatMs(q.totalMs)}</span>
          </li>`).join('')}</ul></section>`;
    } catch (error) {
      const message = error.status === 403 || error.status === 401 ? 'Operator key required to view query records.' : error.message;
      this.innerHTML = `<section class="card"><h2>Recent queries</h2><div class="empty">${esc(message)}</div></section>`;
    }
  }
}

customElements.define('helio-queries', HelioQueries);
