# Engineering review

Three independent reviews were run against the codebase after the first complete build: an architecture and debugging review (reverse engineering the data flow, hunting latent failures), a performance review, and a security audit (reported separately in `SECURITY_AUDIT.md`). Each reviewer worked from the source alone, without seeing the build being produced. This document records what they found, what was changed in response, and what was deliberately left as it was, so that the reasoning is auditable rather than lost in a commit history.

Verification after the fixes: `dotnet build` with warnings as errors, 61 unit tests and 18 integration tests passing, the offline evaluation gate passing at the same quality (24 golden cases, accuracy 91.7%, retrieval hit rate 100%, zero false answers, mean faithfulness 1.00), the web console checked with Playwright (no console errors, no mobile overflow, labelled inputs), and a restart test proving the index rebuilds from stored vectors without a single embedding call.

## 1. Reverse-engineered data flow

The system has four projects with a strict dependency direction: `Helio.Domain` (entities and ports, no dependencies) is referenced by `Helio.Application` (use-case handlers, chunker, prompt, parser, citation guard, evaluation), which is referenced by `Helio.Infrastructure` (EF Core persistence, in-memory and Azure AI Search retrieval, deterministic and Azure OpenAI providers, caching, composition root), which is referenced by `Helio.Api` (minimal API endpoints, authentication, rate limiting, metrics, web console). The evaluation CLI boots the same composition root against an in-memory SQLite database.

### Ingestion (`POST /api/documents`, operator)

1. The endpoint maps the DTO to `IngestRequest` and calls `IngestDocumentHandler`.
2. Validation: `sourceId` (1 to 200 characters, no `::`), `title` (1 to 300), `text` (1 to 2,000,000 characters), `metadata` (at most 20 entries, keys up to 100 and values up to 1,000 characters, 3,500 characters serialised).
3. A per-source semaphore serialises concurrent ingests of the same `sourceId`.
4. Text is Unicode-normalised (NFC) and line endings are normalised. The content hash covers title, canonical metadata and text, so any change is a new version and no change is a no-op (HTTP 200 with `unchanged: true`).
5. `ParagraphChunker` splits on blank lines, drops Markdown heading lines, packs short paragraphs to at least 60 words, splits long paragraphs on sentence boundaries, and hard-splits any single sentence longer than 220 words. A document that yields more than 4,000 chunks is rejected.
6. Chunk ids are `{sourceId}::v{version}::p{ordinal}`. The embedder produces one vector per chunk; this is the only step that keeps the caller's cancellation token.
7. The commit sequence runs on a non-cancellable token: delete the source's old vectors, upsert the new ones, insert the document row with its chunks and their vectors, then bump the shared index generation marker.
8. The endpoint returns 201 with a `Location` of `/api/documents/{sourceId}`, which now exists.

### Answering (`POST /api/ask`, reader)

1. Middleware order: forwarded headers (trusted proxies only), HSTS outside Development, exception handler, security headers, request logging, authentication, authorisation, rate limiting (keyed by principal, or by client address for anonymous callers; a global per-address limiter throttles unauthenticated requests).
2. `AskQuestionHandler` validates and NFC-normalises the question (1 to `MaxQuestionChars`).
3. Cache key = SHA-256 of (index generation, prompt version, chat model version, embedding model version, TopK, CandidateK, MinRelativeScore, lower-cased question). A hit is served with a fresh request id, zero token and cost accounting, and its own audit record with provider `cache`.
4. Embedding and retrieval run under a linked token with the configured `GenerationTimeout`. The in-memory store scores BM25 and cosine over an immutable snapshot and fuses with reciprocal rank fusion; Azure AI Search does the same server-side.
5. Candidates below `MinRelativeScore` of the best are dropped and the top `TopK` are kept. No candidates means an abstention with reason `no_relevant_context` and no model call.
6. The generator produces a JSON draft. If it fails to parse, `RepairAsync` is called once: the chat generator shows the model its previous output, asks for only the JSON object, and doubles the output budget (the dominant failure is truncation, which a verbatim retry at low temperature reproduces).
7. `CitationGuard` strips citations to chunks that were not retrieved and downgrades an uncited answer to an abstention. A model abstention is served with the fixed abstention text; the model's own note goes to `Answer.Detail` for operators, never to the user.
8. The deadline firing (not the caller cancelling) produces an abstention with reason `upstream_timeout`.
9. The audit record is persisted (failure logged, never surfaced), non-abstained answers are cached, metrics are recorded, and `X-Helio-Request-Id` is set.

### Evaluation

The CLI (`evals/Helio.Evaluation`) and the HTTP endpoint (`POST /api/evaluate`) run the same `EvaluationRunner`. Both bypass the answer cache, both accept `forbiddenPhrases` and `allowAbstain`, and forbidden phrases are checked against whatever text would be served, abstention or not. The HTTP path is limited to 50 cases per request and budgeted at 200 cases per operator per ten minutes; it also carries a three-minute server-side deadline. Larger sets belong to the CLI, which is what CI runs.

## 2. Findings and dispositions

Severity is the reviewers' own. Disposition is one of FIXED (changed in this revision and covered by a test where practical), DELIBERATE (left as designed, with the reason), or DEFERRED (agreed, not done yet, with what it would take).

### Correctness and latent failures

| Id | Severity | Finding | Disposition |
|---|---|---|---|
| B1 | High | `InMemoryHybridStore.SearchAsync` scored from a snapshot but looked ids up in the live dictionary; a concurrent re-ingest (delete then upsert) could throw `KeyNotFoundException` and return HTTP 500. | FIXED. The store now publishes an immutable snapshot (entries, document frequencies, total length) under a single volatile reference; readers never touch shared state. Test `Search_IsSafeWhileASourceIsDeletedAndReIngested` interleaves 200 delete/upsert cycles with 200 searches. |
| B2 | High | Ingestion was neither atomic nor serialised: client disconnect between vector upsert and row insert left orphaned, citeable vectors; two concurrent ingests of one source could interleave versions; oversized metadata failed only on SQL Server, after vectors were written. | FIXED. Per-source semaphore; the commit sequence uses `CancellationToken.None`; metadata is validated before any work (test `Ingest_RejectsOversizedMetadata`); chunk count is bounded. Vectors-first ordering is kept (DELIBERATE, see ADR-013): the in-memory index is rebuilt from the database on restart, so a crash between the two writes converges on the next start or the next ingest. |
| B3 / A4 | High | The answer cache key was the question alone; re-ingests and deploys served superseded citations for up to the TTL. | FIXED. Versioned cache key plus a shared index generation marker bumped on every ingest. Test `Reingest_InvalidatesCachedAnswers` ingests v1, asks, ingests v2, and asserts the next answer cites `::v2::`. |
| B4 | Medium | Cache hits reused the original request id, re-recorded the original latency and tokens in metrics, and wrote no audit record. | FIXED. Hits get a fresh version-7 id, zero tokens and cost, measured latency, provider `cache`, and their own record. Metrics skip token and cost for hits. Integration test asserts distinct request ids and zero input tokens. |
| B5 | Medium | `MinRelativeScore` is ineffective under RRF: with k=60 every fused candidate scores at least 42% of the best, so the documented "noise floor" never fires; only the generator's own coverage threshold stops answers from irrelevant context. | DEFERRED. The floor is honest in intent but needs component-level scores (cosine, BM25) exposed on `RetrievedChunk` to be meaningful. The deterministic generator abstains on low coverage today (integration test `Ask_UnanswerableQuestion_Abstains`), and the Azure path relies on the model's instruction to abstain, which the adversarial golden case checks. Planned: carry both raw scores through `RetrievedChunk`, apply an absolute cosine floor before fusion, and add a handler-level test that an unrelated question retrieves nothing regardless of generator. |
| B6 / A3 | High | Prometheus annotations scraped `/metrics` without credentials (401 forever), and the latency alert named `helio_ask_duration_ms_bucket` while the exporter emits `helio_ask_duration_milliseconds_bucket`. Every alert was dead. | FIXED. The API key handler accepts the operator key as `Authorization: Bearer`, the manifests use a `ServiceMonitor` with `authorization.credentials`, the alert expression is corrected, and `Metrics_RequireOperator` asserts the exact series names and the bearer path. |
| B7 / A1 | Medium | `EnsureCreated` ran on every replica at start; the loser of the race crashed. No migrations exist. | FIXED (race) and DEFERRED (migrations). Schema creation now tolerates the race when the schema is present. The `chunks` table gained two columns in this revision, which `EnsureCreated` cannot apply to an existing database; `deploy/sql/001_chunk_vectors_and_index_cleanup.sql` is the idempotent script, and ADR-013 records the plan to move to EF migrations run from the pipeline. |
| B8 | Medium | A single sentence longer than the chunk limit (log dumps, text without recognised terminators, non-Latin prose) became one unbounded chunk and could exceed the embedding model's input limit. | FIXED. Hard split into fixed word windows; sentence regex recognises any Unicode uppercase letter and CJK terminators; whitespace runs are collapsed before splitting. Test `Split_NeverEmitsAChunkLongerThanMaxWordsEvenWithoutSentenceTerminators`. |
| B9 / M-5 | Medium | Azure AI Search upserts went in one batch (service limit 1,000 actions) and deletes fetched at most 1,000 ids, leaving stale chunks citeable. | FIXED (compile-verified only, no live service in this environment). Uploads in batches of 500 with per-document result checks; deletes page until the filter is empty; `Select` excludes the vector from hits; the KNN pool is at least 50. |
| B10 | Medium | The "repair retry" was a verbatim retry. | FIXED. `IAnswerGenerator.RepairAsync` with a corrective message and double output budget; `output_truncated` is reported when the finish reason is length. The extractive generator's repair is a second generation because it always emits valid JSON. |
| B11 | Medium | A model abstention's free text was served verbatim and exempt from the forbidden-phrase gate: the one unguarded output path. | FIXED. Fixed abstention text; model note kept in `Answer.Detail`; the gate checks served text regardless. Test `Apply_NeverServesInjectedTextThroughAnAbstention`. |
| B12 / M-3 | Medium | All anonymous readers shared one rate-limit bucket (the "anonymous" subject) and one caller hash; the address fallback was unreachable. | FIXED. Anonymous callers are keyed by client address; forwarded headers are trusted only from configured proxy networks (`Proxy:TrustedNetworks`). |
| B13 / H-2 / M-2 | High | JWT audience validation was silently disabled when `JwtAudience` was blank; there was no reader role for Entra users; JWT options were read eagerly at registration. | FIXED. Audience is mandatory when an authority is set (start-up validator), `ValidateAudience` is always on, `JwtReaderRole` exists, `MapInboundClaims` is off so `roles` and `oid` keep their wire names, and both JWT options and policy role names are resolved lazily from `IOptions`. Live Entra tokens were not available to test here; the configuration follows Microsoft's documented pattern and fails closed. |
| B14 | Low | A title-only or metadata-only re-ingest was silently dropped. | FIXED. The hash covers title and canonical metadata. Test `TitleChange_IsANewVersion_AndDocumentCanBeFetchedBySourceId`. |
| B15 / L-2 | Medium | `/api/evaluate` returned 500 for a malformed case and could run 500 model calls in one request. | FIXED. A shared endpoint filter maps `ValidationException` to 400 everywhere; cases are validated; 50 per request; per-operator case budget; three-minute deadline. Test `Evaluate_ValidatesCases_AndRunsAgainstTheLivePipeline`. |
| B16 / A10 | Medium | Unknown model versions priced silently at zero; stale `extractive-v2` price key. | FIXED. One warning per unknown version (`ConfiguredCostModel` callback, test `EstimateMicroUsd_ReportsAnUnknownModelOnceSoZeroSpendIsNeverSilent`); price table lists `extractive-v3` and `hashing-512`. |
| B17 | Low | No Unicode normalisation; truncation could split surrogate pairs. | FIXED. NFC at ingest and query time; grapheme-aware truncation (`TruncateGraphemes`) used for excerpts and reports. |
| B18 | Verify | Warm-up used `Include` inside a `GroupBy`, whose translation could drop the chunks and start the service with an empty index. | FIXED. Two plain queries (latest ids, then chunks by id). `RestartTests.Index_IsRebuiltFromStoredVectorsAfterRestart` starts a second host on the same database and answers with a citation. |
| B19 | Low | No timeout around model or embedding calls. | FIXED. `Rag:GenerationTimeout` (default 45 s) on a linked token; the deadline becomes an `upstream_timeout` abstention; a new alert watches the rate. |

### Architecture

| Id | Finding | Disposition |
|---|---|---|
| A2 | Embeddings were not persisted; every replica start re-embedded the corpus (paid calls, startup-probe risk). | FIXED. `chunks.Vector` (float32 little-endian) and `chunks.EmbeddingModel` are written at ingest; warm-up reuses vectors whose model matches and re-embeds only the rest, in one bulk upsert. The restart test asserts zero re-embedding. |
| A5 | In-memory store rebuilt all statistics on every upsert and allocated two full-size arrays per query. | FIXED. Incremental document frequencies and total length; per-query IDF; stored vector norms; sparse score lists. Upserts copy the entry map once (O(N) reference copy) to publish a new snapshot, which is the price of lock-free reads and is paid once per ingest, not per query. |
| A6 | DataAnnotations on DTOs are not evaluated by minimal APIs without `AddValidation()`. | DELIBERATE. Handlers validate every field server-side (the attributes remain as documentation for OpenAPI), and the shared filter guarantees a 400. Enabling the .NET 10 validation source generator was judged not worth a second validation path. |
| A7 | `SecuritySetup` read configuration eagerly. | FIXED. See B13. |
| A8 | Per-process state (rate limiter, in-memory store) with no guard against multi-replica configurations. | FIXED (guard) and DELIBERATE (limiter). `Storage:VectorStore=memory` with a Redis connection string is refused unless `AllowMemoryStoreWithRedis` is set. Rate limits are documented as per replica; a Redis-backed limiter is listed in ADR-013 as the step to take when the number must be exact. |
| A9 | Two evaluators with different semantics. | FIXED. See the evaluation flow above. |
| A11 | The API layer resolved `HelioDbContext` for readiness. | FIXED. `ReadinessCheck` lives in Infrastructure, registered by `AddHelio`, with a five-second result cache and no backend error text in the response. |
| A12 / A13 | Ports defined in handler files; dead `IClock`; unused packages. | FIXED. `IAnswerCache`, `IIndexGeneration`, `ICostModel` and `ValidationException` are in `Helio.Application/Common`; `IClock` and the unused `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Http.Resilience` and `AspNetCore.HealthChecks.UI.Client` references are gone. |
| A14 | Duplicated "latest version per source" query. | FIXED. `DocumentQueries.LatestPerSource` is the single definition. |
| A15 / L-6 | Unbounded `query_records` growth; unsalted caller hash. | FIXED. `Retention:QueryRecordRetention` with an hourly purge service (`ExecuteDeleteAsync`); HMAC-SHA256 pseudonyms with `Security:CallerHashPepper`, required in Production. |
| A16 | Three token-estimation heuristics. | DELIBERATE. They serve different purposes (budget decisions, usage fallback, extractive accounting) and none is billed; unifying them would add a shared abstraction for no behavioural change. |
| A17 | CI live-evaluation job could never run; report file committed. | FIXED. Nightly schedule; OIDC federation instead of a stored key. |
| A18 | Hand-built `IDistributedCache` registration. | FIXED. Single registration with a bounded in-process cache (`MemoryCacheSizeLimitEntries`). |

### Performance review

| Finding | Disposition |
|---|---|
| Answer cache not invalidated on re-ingest | FIXED (B3). |
| `SearchAsync` snapshot race and allocations | FIXED (B1, A5). |
| Audit insert and cache write sequential | DELIBERATE. They are a few hundred microseconds each and the audit write must complete before the cache write can be trusted to describe a recorded answer; parallelising saves nothing measurable. |
| `FindLatestAsync` loaded all chunks | FIXED. Header-plus-count projection. |
| `query_records` random GUID primary key; `Abstained` index | FIXED. Version-7 ids; index dropped. |
| AI Search `Select`, KNN pool, paged deletes | FIXED (B9). |
| Warm-up re-embeds | FIXED (A2). |
| Readiness check uncached | FIXED (A11). |
| Anonymous rate-limit partition | FIXED (B12). |
| `MemoryDistributedCache` unbounded | FIXED (A18). |
| GC settings, ReadyToRun | DELIBERATE. `DOTNET_gcServer=1` and dynamic adaptation are set in the image; ReadyToRun compilation trades image size for start-up time and is left for a measured decision. |
| HPA on CPU only | DELIBERATE. CPU is the right first signal for a request-bound service; a latency-based scaler needs the load-test numbers below. |
| SQLite WAL and busy timeout | DELIBERATE. SQLite is the development and single-node store; SQL Server is the production target. |
| `ApproximateTokens` allocation | FIXED. Counts words without splitting. |
| Console polling while hidden | DEFERRED. The status badge polls every 30 s; pausing on `visibilitychange` is a small follow-up. |
| Price key mismatch | FIXED (B16). |
| Resilience handler referenced but unused | FIXED. Reference removed; the Azure SDK clients carry their own retry policies, and the pipeline has its own deadline (B19). |
| Evaluate endpoint sequential | DELIBERATE. Sequential cases keep the latency numbers in the report honest and the model quota predictable. |

## 3. Tech-lead tradeoffs

- **Vectors before the row, not a distributed transaction.** The database is the system of record and the index is derived; a crash between the two writes is repaired by the next ingest or the next warm-up. A saga across SQL and Azure AI Search would add a reconciler and a status column for a window of a few milliseconds that the per-source lock and the non-cancellable commit already make rare.
- **Snapshot copies on write, lock-free reads.** Ingest is rare and reads are constant; paying an O(N) reference copy per ingest to make every read allocation-light and race-free is the right side of that asymmetry.
- **Cache keyed by everything that changes the answer.** The alternative, explicit invalidation per question, cannot know which cached questions a document change affects. A generation marker in the key makes every re-ingest a clean miss and lets old entries expire on their own.
- **Fail closed on configuration.** Start-up validators refuse anonymous reads, short or placeholder keys, a missing pepper, a missing JWT audience, the deterministic provider, and the memory store behind Redis in Production. The cost is one more environment variable to set deliberately; the benefit is that a copy-pasted compose override cannot open the knowledge base.
- **One authenticated metrics endpoint rather than a second port.** A second Kestrel listener without auth would be simpler for Prometheus but would be one misconfigured Ingress path away from public metrics. Accepting the operator key as a bearer token keeps a single surface with a single policy.
- **Budget evaluation by cases, not requests.** One request can do 50 units of model work; counting requests would make the limit meaningless.

## 4. Load-test plan

Not yet run; this is the plan the HPA thresholds should be tuned from.

1. Corpus: 2,000 documents of 2 to 20 chunks (about 20,000 chunks) ingested through the API with the deterministic provider, then the same with Azure OpenAI and Azure AI Search in a staging resource group.
2. Traffic: k6 with a 70/30 mix of repeated (cacheable) and unique questions, 10 to 400 virtual users ramped over 10 minutes, plus one ingest every 30 seconds to exercise invalidation.
3. Measure: p50/p95/p99 of `helio_ask_duration_milliseconds`, abstention rate by reason, `helio_cost_micro_usd_total`, CPU and memory per pod, Azure OpenAI 429s.
4. Pass criteria: p95 under 2 s for cache misses with the live model, under 50 ms for hits, zero 5xx, no `upstream_timeout` abstentions at the target load.
5. Outputs: the CPU utilisation at which p95 crosses 1.5 s becomes the HPA target; the ingest-during-load run confirms no stale citations (compare cited chunk versions with the document list).
