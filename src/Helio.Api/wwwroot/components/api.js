// Thin typed client for the Helio API. Every component goes through this module so
// authentication, error shaping and the base URL live in exactly one place.

const STORAGE_KEY = 'helio.apiKey';

export class ApiError extends Error {
  constructor(status, title, detail, errors) {
    super(detail || title || `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.errors = errors || {};
  }
}

export function getApiKey() {
  try { return sessionStorage.getItem(STORAGE_KEY) || ''; } catch { return ''; }
}

export function setApiKey(key) {
  try {
    if (key) sessionStorage.setItem(STORAGE_KEY, key); else sessionStorage.removeItem(STORAGE_KEY);
  } catch { /* storage unavailable: the key lives for this page only */ }
  window.dispatchEvent(new CustomEvent('helio:auth-changed'));
}

async function request(method, path, body, signal) {
  const headers = { 'Accept': 'application/json' };
  const key = getApiKey();
  if (key) headers['X-Api-Key'] = key;
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  const response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = { detail: text }; }
  }

  if (!response.ok) {
    const title = data?.title || response.statusText;
    const detail = data?.detail || (response.status === 401 ? 'An API key is required.' : response.status === 403 ? 'This key is not allowed to do that.' : response.status === 429 ? 'Too many requests. Please wait a minute.' : null);
    throw new ApiError(response.status, title, detail, data?.errors);
  }

  return { data, headers: response.headers };
}

export const api = {
  ask: (question, signal) => request('POST', '/api/ask', { question }, signal),
  ingest: (doc, signal) => request('POST', '/api/documents', doc, signal),
  documents: (signal) => request('GET', '/api/documents', undefined, signal),
  queries: (take, signal) => request('GET', `/api/queries?take=${encodeURIComponent(take)}`, undefined, signal),
  ready: (signal) => request('GET', '/health/ready', undefined, signal),
};

/** Escape text for safe insertion into innerHTML. Components use this for every string from the API. */
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function formatMs(ms) { return `${Math.round(ms)} ms`; }
export function formatUsd(usd) { return usd < 0.0001 ? '< $0.0001' : `$${usd.toFixed(4)}`; }
export function formatPercent(x) { return `${Math.round(x * 100)}%`; }
