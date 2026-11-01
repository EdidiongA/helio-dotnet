# Helio.NET Architecture

## 1. System architecture

Helio.NET is a four-project clean architecture. Dependencies point inwards only.

| Project | Depends on | Responsibility |
|---|---|---|
| `Helio.Domain` | nothing | Entities (`Document`, `Chunk`, `Answer`, `QueryRecord`), evaluation records, and the ports: `IChunker`, `ITextEmbedder`, `IVectorStore`, `IAnswerGenerator`, `IDocumentRepository`, `IQueryRecordRepository`, `IClock` |
| `Helio.Application` | Domain | Use cases: `IngestDocumentHandler`, `AskQuestionHandler`, `EvaluationRunner`; text normalisation and chunking; prompt building; draft parsing; the citation guard; options |
| `Helio.Infrastructure` | Application | Adapters: EF Core persistence (SQLite, SQL Server), `InMemoryHybridStore`, `AzureAiSearchStore`, `HashingEmbedder`, `ExtractiveAnswerGenerator`, Azure OpenAI providers over Microsoft.Extensions.AI, distributed answer cache, cost model, index warm-up, the composition root |
| `Helio.Api` | Infrastructure | Minimal API endpoints and DTOs, authentication and authorization, rate limiting, security headers, OpenTelemetry metrics, Serilog, health checks, and the static web console |

Supporting projects: `Helio.UnitTests` (xUnit, FsCheck), `Helio.IntegrationTests` (WebApplicationFactory against the real host), `Helio.Evaluation` (console runner and CI gate).

### Runtime topology

```
client ──TLS──► ingress / Container Apps ──► Helio.Api (N replicas, stateless)
                                                  │
                        ┌─────────────────────────┼────────────────────────┐
                        ▼                         ▼                        ▼
                 Azure OpenAI               Azure AI Search            SQL Server
            (chat + embeddings,          (hybrid index, HNSW,      (documents, chunks,
             managed identity)            security trimming)        query_records)
                        │
                        ▼
                      Redis (answer cache, shared across replicas)
```

Single-node mode replaces Azure AI Search with the in-process hybrid index (rebuilt from SQL at start-up), SQL Server with SQLite, and Redis with the in-process cache. The code paths are identical; only configuration changes.

## 2. Data flow

### Ingestion (`POST /api/documents`)

1. Endpoint validates the DTO and the operator role, then calls `IngestDocumentHandler`.
2. Text is normalised (line endings) and hashed (SHA-256).
3. `FindLatestAsync(sourceId)` loads the current version; identical hash returns `Unchanged` without touching the index.
4. `ParagraphChunker` splits on blank lines, drops markdown headings, packs short paragraphs to at least 60 words and splits long ones on sentence boundaries to at most 220 words.
5. Chunk ids are minted as `{sourceId}::v{version}::p{ordinal}`, stable for audit.
6. `ITextEmbedder.EmbedAsync` produces one vector per chunk (batched for Azure OpenAI).
7. `IVectorStore.DeleteBySourceAsync` then `UpsertAsync` replace the previous version's vectors.
8. The document row and its chunk rows are written through EF Core.

Order matters: vectors first, then the row. A crash between the two leaves vectors the next ingest overwrites; the reverse order could leave a document retrieval cannot see.

### Answering (`POST /api/ask`)

1. Endpoint validates, authorises (reader), applies the per-caller sliding window limit, and calls `AskQuestionHandler` with a pseudonymous caller hash.
2. Question length is checked; the answer cache (SHA-256 of the lower-cased question) is consulted.
3. The question is embedded; `IVectorStore.SearchAsync(question, vector, CandidateK)` returns fused candidates.
4. Candidates below `MinRelativeScore × best` are dropped; the top `TopK` remain. No candidates means an immediate abstention with reason `no_relevant_context` and no model call.
5. `IAnswerGenerator.GenerateAsync` builds the grounded prompt and returns raw text plus a parsed `ModelDraft` (or a parse error). One repair retry is attempted on a parse failure; token counts are summed.
6. `CitationGuard.Apply` strips citations that are not in the retrieved set and downgrades an answer with none to an abstention (`no_citations` or `citations_not_in_context`).
7. A `QueryRecord` (question, retrieved ids, cited ids, provider, model, prompt version, timings, tokens, cost, caller hash) is persisted. A persistence failure is logged and never fails the request.
8. Non-abstained answers are cached for `AnswerCacheTtl`. Metrics are recorded; the response carries the full trace.

### Evaluation (`POST /api/evaluate`, `Helio.Evaluation`)

Each golden case is sent through the real handler. Retrieval is judged first (expected source present?), then the answer is classified into the six-way taxonomy; faithfulness and relevance come from `IAnswerJudge` (lexical by default, LLM-judged when wired to a chat client). The console runner prints the report and exits non-zero when a gate fails.

## 3. File structure

```
helio-dotnet/
├── Directory.Build.props          # warnings as errors, latest analyzers, deterministic builds
├── Helio.slnx
├── Dockerfile · docker-compose.yml · .dockerignore · .editorconfig · .gitignore
├── corpus/                        # 8 reference documents (home solar engineering)
├── evals/
│   ├── golden/golden.json         # 24 cases incl. unanswerable and injection canaries
│   └── Helio.Evaluation/          # runner + CI gates
├── deploy/
│   ├── k8s/helio.yaml · alerts.yaml
│   └── azure/main.bicep
├── .github/workflows/ci.yml
├── docs/                          # this file, reviews, audit, deployment, UI, decisions
├── src/
│   ├── Helio.Domain/              Documents.cs · Answers.cs · Ports.cs · Evaluation.cs
│   ├── Helio.Application/
│   │   ├── Options.cs
│   │   ├── Text/                  TextNormalizer.cs · ParagraphChunker.cs
│   │   ├── Answering/             AskQuestionHandler.cs · CitationGuard.cs · PromptBuilder.cs · DraftParser.cs
│   │   ├── Ingestion/             IngestDocumentHandler.cs
│   │   └── Evaluation/            EvaluationRunner.cs (+ LexicalAnswerJudge)
│   ├── Helio.Infrastructure/
│   │   ├── DependencyInjection.cs (composition root, options)
│   │   ├── Ai/                    DeterministicProvider.cs (HashingEmbedder, cost model) · ExtractiveAnswerGenerator.cs · AzureOpenAiProvider.cs
│   │   ├── Retrieval/             InMemoryHybridStore.cs · AzureAiSearchStore.cs · IndexWarmupService.cs
│   │   ├── Persistence/           HelioDbContext.cs · Repositories.cs
│   │   └── Caching/               DistributedAnswerCache.cs
│   └── Helio.Api/
│       ├── Program.cs · HelioMetrics.cs · appsettings*.json
│       ├── Security/SecuritySetup.cs
│       ├── Endpoints/HelioEndpoints.cs · Contracts/Dtos.cs
│       └── wwwroot/               index.html · styles.css · favicon.svg · components/*.js
└── tests/
    ├── Directory.Build.props
    ├── Helio.UnitTests/           TextTests · AnsweringTests · InfrastructureTests (50 tests)
    └── Helio.IntegrationTests/    ApiTests (13 tests over the real host)
```

## 4. Database schema

EF Core, same model on SQLite and SQL Server. Timestamps are stored as UTC `DateTime` because SQLite cannot order `DateTimeOffset` columns.

**documents**

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | |
| SourceId | nvarchar(200) | caller-chosen stable id |
| Title | nvarchar(300) | |
| Version | int | unique with SourceId |
| ContentHash | nvarchar(64) | SHA-256 hex; unchanged re-ingests are skipped |
| IngestedAtUtc | datetime2 | |
| MetadataJson | nvarchar(4000) null | flat key/value metadata |

Index: unique `(SourceId, Version)`.

**chunks**

| Column | Type | Notes |
|---|---|---|
| Id | nvarchar(260) PK | `{sourceId}::v{version}::p{ordinal}` |
| DocumentId | uniqueidentifier FK → documents (cascade) | indexed |
| SourceId | nvarchar(200) | denormalised for retrieval results |
| Title | nvarchar(300) | |
| Ordinal | int | |
| Text | nvarchar(max) | system of record |
| EmbeddingModel | nvarchar(100) null | which model produced `Vector`; a mismatch at warm-up re-embeds that chunk only |
| Vector | varbinary(max) null | float32 little-endian embedding, so a restart rebuilds the index without model calls |

**query_records**

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | version-7 (time-ordered) request id returned in `X-Helio-Request-Id` |
| Question | nvarchar(4000) | |
| Answer | nvarchar(max) | |
| Abstained | bit | |
| Confidence | float | |
| RetrievedChunkIdsJson, CitedChunkIdsJson | nvarchar(8000) | JSON arrays |
| Provider, ModelVersion, PromptVersion | nvarchar | which model and prompt produced it |
| RetrievalMs, GenerationMs, TotalMs | int | |
| InputTokens, OutputTokens | int | |
| CostMicroUsd | bigint | 1e-6 USD, integer to avoid floating-point money |
| Reason | nvarchar(100) null | abstention reason |
| CallerHash | nvarchar(64) null | HMAC-SHA256 prefix of the principal (keyed by `Security:CallerHashPepper`); never the identity |
| CreatedAtUtc | datetime2 | indexed |

Retention: `Retention:QueryRecordRetention` (zero keeps records forever) drives an hourly purge service; cache hits are recorded too, with provider `cache`.

**Azure AI Search index** `helio-chunks`: `id` (key, URL-safe base64 of the chunk id), `chunkId`, `documentId`, `sourceId` (filterable), `title` (searchable), `ordinal`, `text` (searchable), `vector` (HNSW profile, dimensions from the embedder).

## 5. API contracts

See README for the endpoint table. Request and response shapes live in `src/Helio.Api/Contracts/Dtos.cs` and the generated OpenAPI document at `/openapi/v1.json`. Errors use RFC 9457 problem details; validation errors carry an `errors` map keyed by field.

`POST /api/ask` response (abridged):

```json
{
  "requestId": "…", "answer": "…", "abstained": false, "reason": null, "confidence": 0.91,
  "citations": [{ "chunkId": "04_wiring_and_protection::v1::p1", "sourceId": "…", "title": "…", "excerpt": "…" }],
  "retrieved": [{ "chunkId": "…", "sourceId": "…", "title": "…", "score": 0.0328 }],
  "provider": "azure-openai", "modelVersion": "gpt-4o-mini", "promptVersion": "helio-grounded-v4",
  "retrievalMs": 41, "generationMs": 820, "totalMs": 870, "inputTokens": 471, "outputTokens": 71,
  "estimatedCostUsd": 0.000113, "fromCache": false
}
```

## 6. UI architecture

The console is six native web components in light DOM (so one stylesheet with design tokens styles everything), loaded as ES modules with no build step: `helio-status`, `helio-ask`, `helio-answer`, `helio-documents`, `helio-ingest`, `helio-queries`. They communicate by DOM events (`helio:asking`, `helio:answered`, `helio:failed`, `helio:ingested`, `helio:auth-changed`), and all HTTP goes through one typed client (`components/api.js`) that owns authentication and error shaping. Every component implements loading, empty, error and success states, escapes all API strings before rendering, and is keyboard and screen-reader accessible. Details: `docs/UI.md`.

## 7. Cross-cutting concerns

- **Configuration**: options classes with data annotations, validated on start; every provider resolved lazily from `IOptions<T>`.
- **Observability**: Serilog compact JSON to stdout; OpenTelemetry meter `Helio` (`helio.answers`, `helio.abstentions{reason}`, `helio.ask.duration`, `helio.ask.input_tokens`, `helio.cost.micro_usd`, `helio.ingests`) plus ASP.NET Core and runtime instrumentation, exported for Prometheus at `/metrics`.
- **Resilience**: SQL Server retry-on-failure; one repair retry on malformed model output; audit persistence failures never fail a request; startup probe waits for index warm-up.
- **Security**: see `docs/SECURITY_AUDIT.md`.
