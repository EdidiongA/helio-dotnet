# Architecture Decision Records

Each record states the decision, the alternatives, and the trade-off accepted. These are the questions a technical lead would ask before writing code, with the answers chosen for a product expected to live five years.

## ADR-001: Clean architecture with ports in the domain

**Decision.** Four projects with inward-only dependencies; the domain declares the ports (`IVectorStore`, `IAnswerGenerator`, …) and infrastructure implements them.

**Alternatives.** A single project (faster to start, but every provider change touches the pipeline); vertical slices (good for large feature sets, overkill for five use cases).

**Trade-off.** More files and some mapping code (domain entities to EF rows, to DTOs). Accepted because providers are the part most likely to change over five years: models, search services and databases all have swappable adapters already.

## ADR-002: Deterministic offline provider as a first-class citizen

**Decision.** The pipeline runs end to end with a hashing embedder and an extractive generator that emit the same contracts as Azure OpenAI.

**Alternatives.** Mock the model with canned responses (does not exercise parsing, citations or abstention); require live keys for tests (slow, costly, non-reproducible).

**Trade-off.** The deterministic provider is not semantically capable; two paraphrase cases in the golden set fail with it and are documented. Accepted because every unit test, integration test and the CI quality gate run in seconds at zero cost, and the live provider is validated nightly on the same golden set.

## ADR-003: Hybrid retrieval with reciprocal rank fusion

**Decision.** BM25 and vector similarity ranked separately and fused by RRF (k = 60), both in process and on Azure AI Search.

**Alternatives.** Vector-only (misses exact part numbers and fault codes that users quote); keyword-only (misses paraphrase); weighted score blending (requires calibrating incompatible score scales).

**Trade-off.** Two rankings per query. Accepted because reference material is full of exact tokens and RRF needs no calibration.

## ADR-004: Paragraph chunking rather than fixed token windows

**Decision.** Split on blank lines, pack to 60 to 220 words, split long paragraphs on sentences, drop markdown headings.

**Trade-off.** Chunk sizes vary. Accepted because numeric claims stay attached to their qualifiers and citations remain meaningful to a reader.

## ADR-005: Citations constrained to the retrieved set, enforced in code

**Decision.** The pipeline, not the prompt, guarantees that every citation refers to a chunk retrieved for this request, and downgrades uncited answers to abstentions.

**Trade-off.** Occasionally a correct answer is withheld because the model forgot to cite. Accepted: a withheld correct answer is recoverable; a fluent answer "grounded" in a document that was never retrieved destroys trust.

## ADR-006: JSON contract with one repair retry

**Decision.** The model must reply with a single JSON object; a parse failure triggers exactly one retry, then a `schema_violation` abstention.

**Alternatives.** Unlimited retries (unbounded cost); accept free text (no validation possible).

**Trade-off.** One extra model call in the worst case. Accepted and metered: schema violations are a counter with an alert rule.

## ADR-007: System of record is SQL; the vector index is derived

**Decision.** Chunk text lives in SQL; the in-process index is rebuilt from SQL at start-up; Azure AI Search is the shared derived index for multi-replica deployments.

**Trade-off.** Warm-up re-embeds every chunk with the deterministic embedder (cheap) and would need persisted vectors for a large corpus with Azure OpenAI embeddings. Accepted for now with the persisted-vector column listed as the first scaling step in DEPLOYMENT.md.

## ADR-008: Options resolved lazily from `IOptions<T>`

**Decision.** No provider decision reads `IConfiguration` during registration.

**Why.** Configuration layered on after registration (test hosts, some orchestrators) was silently ignored by eager reads; this surfaced in the integration tests as a database path that would not override. Lazy resolution removes the whole class of bug.

## ADR-009: API keys plus optional Entra ID

**Decision.** Service-to-service and local use authenticate with `X-Api-Key` (constant-time comparison, fingerprint only in logs); interactive users authenticate with Entra ID bearer tokens when configured. Roles: reader, operator.

**Trade-off.** Two schemes to maintain. Accepted because an enterprise deployment needs SSO and a pipeline needs keys.

## ADR-010: Money as integer micro-dollars

**Decision.** Cost is computed from configured per-million-token prices and stored as `long` micro-USD.

**Why.** Floating-point money accumulates rounding error in aggregates; an integer column sums exactly.

## ADR-011: Web components, no build step

**Decision.** The console is six native web components in light DOM, served from `wwwroot`.

**Alternatives.** Blazor (natural for a .NET shop, but adds a WebAssembly payload or a SignalR circuit for a six-widget console); React (a build pipeline for a small page).

**Trade-off.** No component library. Accepted because the console is an operator tool whose value is transparency, and a dependency-free page is the easiest thing for a .NET team to own for five years.

## ADR-012: What was deliberately not built

- Streaming answers (SSE): useful UX, orthogonal to correctness; the `IChatClient` abstraction supports it when wanted.
- Multi-agent orchestration: the posting's agent scenarios belong above this service; Helio exposes a clean `ask` contract an agent (Semantic Kernel, Microsoft Agent Framework) can call as a tool.
- Reranker model: Azure AI Search's semantic ranker is one configuration flag away; the in-process store does not need it at its scale.
- Per-document access control lists: designed for (security trimming note in `AzureAiSearchStore`), not implemented, because the corpus has no classified documents yet.

## ADR-013: Schema evolution and derived state after the engineering review

**Context.** The review (see `ENGINEERING_REVIEW.md`) found that the index was re-derived at every start, that `EnsureCreated` cannot evolve a schema, and that per-process state needed guards.

**Decisions.**

- Chunk embeddings are persisted (`chunks.Vector`, `chunks.EmbeddingModel`). The in-memory index is still derived state, but deriving it is now a read, not a paid model call; only chunks embedded by a different model are re-embedded at start.
- Schema creation stays `EnsureCreated` for developer and single-node use, tolerant of the multi-replica race. Any schema change ships with an idempotent SQL script under `deploy/sql/` (the first is `001_chunk_vectors_and_index_cleanup.sql`), to be run from the pipeline before the new image rolls. The next step, when a second change arrives, is an EF migrations project with `MigrateAsync` run by a one-off job rather than by every replica.
- Ingest ordering stays vectors-first, serialised per source, with a non-cancellable commit. The database is the system of record and the next warm-up or ingest repairs a crash between the two writes; a saga was judged more machinery than the window justifies.
- The answer cache key includes a shared index generation marker that ingestion bumps; cached answers therefore never outlive the index they came from.
- Rate limits are per replica by design (in-process limiters). This is acceptable for cost control; when a strict global quota is required, replace the partitioned limiters with a Redis-backed implementation behind the same policies.
- Configuration fails closed: start-up validators refuse insecure or inconsistent settings in Production rather than degrading silently.

**Consequences.** Start-up is cheap and deterministic; a re-ingest is visible to readers immediately; operators must run one script when upgrading an existing database to this revision.
