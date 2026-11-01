import { api, esc } from './api.js';

/**
 * <helio-documents>: lists the latest version of every document in the knowledge base.
 * Refreshes on 'helio:ingested' and 'helio:auth-changed'.
 * States: loading, empty, loaded, error.
 */
class HelioDocuments extends HTMLElement {
  connectedCallback() {
    this.load();
    this._refresh = () => this.load();
    document.addEventListener('helio:ingested', this._refresh);
    window.addEventListener('helio:auth-changed', this._refresh);
  }

  disconnectedCallback() {
    document.removeEventListener('helio:ingested', this._refresh);
    window.removeEventListener('helio:auth-changed', this._refresh);
  }

  async load() {
    this.innerHTML = `<section class="card" aria-busy="true"><h2>Knowledge base</h2><div class="skeleton"></div><div class="skeleton" style="margin-top:8px;width:80%"></div></section>`;
    try {
      const { data } = await api.documents();
      if (!data.length) {
        this.innerHTML = `<section class="card"><h2>Knowledge base</h2><div class="empty">No documents yet. Ingest one below (operator key required).</div></section>`;
        return;
      }
      this.innerHTML = `<section class="card"><h2>Knowledge base <span class="badge">${data.length} document${data.length === 1 ? '' : 's'}</span></h2>
        <ul class="list">${data.map(d => `<li><span>${esc(d.title)}</span><span class="meta">v${d.version} · ${d.chunkCount} chunk${d.chunkCount === 1 ? '' : 's'} · ${esc(new Date(d.ingestedAt).toLocaleDateString())}</span></li>`).join('')}</ul></section>`;
    } catch (error) {
      const hint = error.status === 401 ? 'Set an API key to view documents.' : error.message;
      this.innerHTML = `<section class="card"><h2>Knowledge base</h2><div class="alert alert-bad" role="alert">${esc(hint)}</div></section>`;
    }
  }
}

customElements.define('helio-documents', HelioDocuments);
