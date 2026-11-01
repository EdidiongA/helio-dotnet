import { esc, formatMs, formatUsd, formatPercent } from './api.js';

/**
 * <helio-answer>: renders the answer, citations and the retrieval trace.
 * Listens to 'helio:asking', 'helio:answered', 'helio:failed' bubbled from <helio-ask>.
 * States: empty (before any question), loading (skeleton), answered, abstained, error, cancelled.
 */
class HelioAnswer extends HTMLElement {
  connectedCallback() {
    this.renderEmpty();
    const root = this.closest('main') || document;
    root.addEventListener('helio:asking', () => this.renderLoading());
    root.addEventListener('helio:answered', e => this.renderAnswer(e.detail.response));
    root.addEventListener('helio:failed', e => e.detail.cancelled ? this.renderEmpty('Request cancelled.') : this.renderError(e.detail.error));
  }

  renderEmpty(note = '') {
    this.innerHTML = `<section class="card" aria-live="polite"><h2>Answer</h2><div class="empty">${esc(note) || 'Ask a question to see an answer with citations, retrieval scores, latency and cost.'}</div></section>`;
  }

  renderLoading() {
    this.innerHTML = `<section class="card" aria-busy="true" aria-live="polite"><h2>Answer</h2>
      <div class="skeleton" style="width:90%"></div><div class="skeleton" style="width:70%;margin-top:8px"></div><div class="skeleton" style="width:40%;margin-top:8px"></div>
      <span class="visually-hidden">Loading answer</span></section>`;
  }

  renderError(error) {
    const message = error?.message || 'Something went wrong.';
    const hint = error?.status === 401 ? ' Set an API key from the top bar.' : '';
    this.innerHTML = `<section class="card" aria-live="assertive"><h2>Answer</h2><div class="alert alert-bad" role="alert">${esc(message)}${esc(hint)}</div></section>`;
  }

  renderAnswer(r) {
    const abstained = r.abstained;
    const badge = abstained
      ? `<span class="badge badge-warn">abstained${r.reason ? `: ${esc(r.reason)}` : ''}</span>`
      : `<span class="badge badge-ok">answered, confidence ${formatPercent(r.confidence)}</span>`;
    const cached = r.fromCache ? `<span class="badge">cached</span>` : '';

    const citations = r.citations.length
      ? r.citations.map((c, i) => `
          <div class="citation" id="cite-${i + 1}">
            <div class="title">[${i + 1}] ${esc(c.title)} <span class="muted small mono">${esc(c.chunkId)}</span></div>
            <div class="excerpt">${esc(c.excerpt)}</div>
          </div>`).join('')
      : '';

    const citedIds = new Set(r.citations.map(c => c.chunkId));
    const retrieved = r.retrieved.length ? `
      <details class="retrieved">
        <summary>Retrieval trace: ${r.retrieved.length} passages considered, ${r.citations.length} cited</summary>
        <table>
          <thead><tr><th scope="col">Chunk</th><th scope="col">Source</th><th scope="col">Score</th><th scope="col">Cited</th></tr></thead>
          <tbody>${r.retrieved.map(x => `<tr class="${citedIds.has(x.chunkId) ? 'cited' : ''}"><td class="mono">${esc(x.chunkId)}</td><td>${esc(x.title)}</td><td>${x.score.toFixed(4)}</td><td>${citedIds.has(x.chunkId) ? 'yes' : 'no'}</td></tr>`).join('')}</tbody>
        </table>
      </details>` : '';

    this.innerHTML = `
      <section class="card" aria-live="polite">
        <h2>Answer <span>${badge} ${cached}</span></h2>
        ${abstained ? `<div class="alert alert-warn">${esc(r.answer)}</div>` : `<p class="answer-text">${esc(r.answer)}</p>`}
        ${citations ? `<h3 class="small muted" style="margin:8px 0">Sources</h3>${citations}` : ''}
        <div class="trace" aria-label="Request trace">
          <div class="stat"><div class="k">Retrieval</div><div class="v">${formatMs(r.retrievalMs)}</div></div>
          <div class="stat"><div class="k">Generation</div><div class="v">${formatMs(r.generationMs)}</div></div>
          <div class="stat"><div class="k">Total</div><div class="v">${formatMs(r.totalMs)}</div></div>
          <div class="stat"><div class="k">Tokens in / out</div><div class="v">${r.inputTokens} / ${r.outputTokens}</div></div>
          <div class="stat"><div class="k">Est. cost</div><div class="v">${formatUsd(r.estimatedCostUsd)}</div></div>
          <div class="stat"><div class="k">Model</div><div class="v small">${esc(r.provider)} / ${esc(r.modelVersion)}</div></div>
        </div>
        ${retrieved}
        <p class="muted small mono" style="margin:12px 0 0">request ${esc(r.requestId)} · prompt ${esc(r.promptVersion)}</p>
      </section>`;
  }
}

customElements.define('helio-answer', HelioAnswer);
