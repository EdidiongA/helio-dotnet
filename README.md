# Helio.NET

**Grounded answers with receipts.** A production-grade retrieval-augmented generation (RAG) service in C# and .NET 10: every answer cites the passages it came from, abstains when the evidence is weak, and ships with an evaluation harness that blocks releases when quality regresses.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![C# 14](https://img.shields.io/badge/C%23-14-239120)
![Azure OpenAI](https://img.shields.io/badge/Azure%20OpenAI-ready-0078D4?logo=microsoftazure&logoColor=white)
![Azure AI Search](https://img.shields.io/badge/Azure%20AI%20Search-hybrid-0078D4)
![Tests](https://img.shields.io/badge/tests-79%20passing-2f7d4f)
![Eval](https://img.shields.io/badge/golden%20set-91.7%25%20accuracy%2C%200%20false%20answers-C9A25A)
![License](https://img.shields.io/badge/license-MIT-blue)

Helio.NET is the C#/.NET port of [Helio](https://github.com/EdidiongA/production-ai-systems), rebuilt on Microsoft.Extensions.AI, Azure OpenAI and Azure AI Search, with the same discipline: the pipeline runs fully offline against a deterministic provider so the evaluation suite is reproducible and costs nothing in CI, and switches to Azure OpenAI by configuration.

---

## Contents

- [What it does](#what-it-does)
- [Quick start](#quick-start)
- [Architecture](#architecture)
- [API](#api)
- [Evaluation](#evaluation)
- [Configuration](#configuration)
- [Deployment](#deployment)
- [Engineering documents](#engineering-documents)
- [Where this breaks and why](#where-this-breaks-and-why)
- [Author](#author)
- [License](#license)

---

## What it does

| Capability | How |
|---|---|
| Ingest documents | Paragraph-aware chunking, content hashing (unchanged re-ingests are skipped), versioned chunk ids that stay stable for audit |
| Hybrid retrieval | BM25 keyword search fused with vector similarity by reciprocal rank fusion, in process or on Azure AI Search |
| Grounded generation | Strict JSON contract from the model, parsed and validated with one repair retry; prompt treats passage text as data, not instructions |
| Citation guard | Every citation must point to a chunk retrieved for this request; an answer with no valid citation is downgraded to an abstention |
| Abstention | The system says "not enough evidence" instead of guessing, and records why |
| Evaluation | Golden set with a six-way failure taxonomy, faithfulness and relevance scoring, CI quality gates |
| Operations | API keys and Entra ID, per-caller rate limits, audit records per query, cost per request, OpenTelemetry metrics, structured logs, readiness probes |
| Console | Accessible web console (web components, no build step) showing the answer, citations, retrieval trace, latency and cost |

## Quick start

Requires the .NET 10 SDK. Everything below runs offline with the deterministic provider.

```bash
git clone https://github.com/EdidiongA/helio-dotnet.git
cd helio-dotnet
dotnet test                                                   # 79 unit and integration tests
dotnet run --project evals/Helio.Evaluation -- corpus evals/golden/golden.json   # quality gates
dotnet run --project src/Helio.Api                            # http://localhost:5111
```

Then open the console at http://localhost:5111, set the operator key from `appsettings.Development.json` (`dev-operator-key-local-only-5f3c9a1e7b2d`) and ingest the eight documents in `corpus/`, or load them from the command line:

```bash
for f in corpus/*.md; do
  id=$(basename "$f" .md); title=$(head -1 "$f" | sed 's/^# //')
  jq -n --arg id "$id" --arg title "$title" --rawfile text "$f" '{sourceId:$id,title:$title,text:$text}' \
    | curl -s -H "X-Api-Key: dev-operator-key-local-only-5f3c9a1e7b2d" -H "Content-Type: application/json" -d @- http://localhost:5111/api/documents
done
curl -s -H "Content-Type: application/json" -d '{"question":"What fuse should be used on the positive terminal of a lithium battery bank?"}' http://localhost:5111/api/ask | jq .
```

To run against Azure OpenAI, set `Ai__Provider=azure-openai` and `Ai__Endpoint` (managed identity) or `Ai__ApiKey` (local development). To share the index across replicas, set `Storage__VectorStore=azure-ai-search` and `Storage__SearchEndpoint`.

## Architecture

```
                 ┌────────────────────────── Helio.Api ───────────────────────────┐
  Console  ───►  │ Minimal APIs · API key / Entra ID · rate limits · OpenTelemetry │
  (web components)│ ProblemDetails · health · Serilog JSON · security headers      │
                 └──────────────┬──────────────────────────────┬─────────────────┘
                                │                              │
                 ┌──────────────▼───────── Helio.Application ──▼─────────────────┐
                 │ IngestDocumentHandler      AskQuestionHandler      EvaluationRunner │
                 │ normalise → hash → chunk   embed → hybrid retrieve → filter         │
                 │ → embed → upsert → persist → generate → parse (repair once)         │
                 │                            → CitationGuard → record → cache         │
                 └──────────────┬──────────────────────────────┬─────────────────┘
                                │   ports (Helio.Domain)       │
       ┌────────────────────────▼──────────────────────────────▼────────────────────┐
       │                            Helio.Infrastructure                             │
       │ ITextEmbedder      HashingEmbedder | Azure OpenAI embeddings (M.E.AI)       │
       │ IAnswerGenerator   ExtractiveAnswerGenerator | Azure OpenAI chat (M.E.AI)   │
       │ IVectorStore       InMemoryHybridStore (BM25 + cosine, RRF) | Azure AI Search│
       │ Repositories       EF Core: SQLite (dev/test) or SQL Server (prod)          │
       │ IAnswerCache       IDistributedCache: in-process or Redis                   │
       └─────────────────────────────────────────────────────────────────────────────┘
```

Dependencies point inwards: `Helio.Domain` has no references, `Helio.Application` depends only on the domain, and `Helio.Infrastructure` implements the ports. The composition root resolves every provider lazily from `IOptions<T>`, so configuration layered on by a test host or an orchestrator is honoured.

The full design, data flow and schema are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## API

| Method | Path | Role | Purpose |
|---|---|---|---|
| POST | `/api/ask` | reader | Answer a question with citations, or abstain. Returns the retrieval trace, latency, tokens and estimated cost. |
| POST | `/api/documents` | operator | Ingest or re-ingest a document. `201` when indexed, `200` with `unchanged: true` when the content hash matches. |
| GET | `/api/documents` | reader | Latest version of every document. |
| GET | `/api/queries?take=50` | operator | Recent query records: what was asked, retrieved, cited, and what it cost. |
| POST | `/api/evaluate` | operator | Run a golden set through the live pipeline; returns the six-way outcome breakdown. |
| GET | `/health/live`, `/health/ready` | anonymous | Liveness; readiness reports the number of indexed chunks. |
| GET | `/metrics` | operator | Prometheus exposition of Helio's domain metrics. |
| GET | `/openapi/v1.json` | anonymous | OpenAPI document. |

Authentication is by `X-Api-Key` (reader or operator keys, compared in constant time) or by an Entra ID bearer token when `Security:JwtAuthority` is set. Validation errors return RFC 9457 problem details.

## Evaluation

`dotnet run --project evals/Helio.Evaluation` ingests the corpus, runs the 24-question golden set and classifies every case:

| Outcome | Meaning |
|---|---|
| Correct | Retrieved the right source and the answer contains an expected keyphrase |
| CorrectAbstention | Abstained on an unanswerable or adversarial question |
| RetrievalFailure | The expected source was not in the retrieved set |
| GenerationFailure | Right source retrieved, wrong or missing answer |
| CitationMismatch | A citation pointed outside the retrieved set (the guard makes this impossible in practice; it is kept to prove that) |
| FalseAnswer | Answered when it should have abstained, or echoed a forbidden phrase |

Current result with the deterministic provider: **24 cases, accuracy 91.7%, retrieval hit rate 100%, zero false answers, mean faithfulness 1.00, p95 latency 1 ms**. The gates (accuracy ≥ 0.85, hit rate ≥ 0.95, zero false answers, faithfulness ≥ 0.90, p95 < 2 s) fail the CI build when breached. The same runner executes against Azure OpenAI in the nightly `live-evaluation` job.

The golden set supports `forbiddenPhrases` (for prompt-injection canaries: a served answer containing an injected value is a FalseAnswer) and `allowAbstain` (refusing an adversarial question is acceptable).

## Configuration

All settings are bound from `appsettings.json`, environment variables (`Section__Key`) or user secrets, and validated at start-up.

| Section | Key | Default | Notes |
|---|---|---|---|
| `Rag` | `TopK` | 4 | Chunks passed to the generator |
| `Rag` | `CandidateK` | 12 | Candidates retrieved before fusion |
| `Rag` | `MinRelativeScore` | 0.25 | Drop candidates below this fraction of the best score |
| `Rag` | `AnswerCacheTtl` | 10 min | `00:00:00` disables the answer cache |
| `Ai` | `Provider` | `deterministic` | or `azure-openai` |
| `Ai` | `Endpoint`, `ApiKey`, `ChatDeployment`, `EmbeddingDeployment` | | Managed identity is used when `ApiKey` is empty |
| `Ai` | `Prices` | | `"model": "inputUsdPerMillion/outputUsdPerMillion"` |
| `Storage` | `Database` | `sqlite` | or `sqlserver` |
| `Storage` | `VectorStore` | `memory` | or `azure-ai-search` |
| `Storage` | `RedisConnectionString` | | Empty uses the in-process cache |
| `Security` | `ReaderApiKeys`, `OperatorApiKeys` | | Arrays of keys |
| `Security` | `AllowAnonymousReads` | false | Development only |
| `Security` | `JwtAuthority`, `JwtAudience`, `JwtOperatorRole` | | Entra ID |
| `Security` | `AskPermitPerMinute`, `IngestPermitPerMinute` | 60, 10 | Per caller |

## Deployment

- `Dockerfile`: multi-stage build onto the chiseled, non-root ASP.NET image.
- `docker-compose.yml`: API + Redis + SQL Server for a production-like local stack.
- `deploy/k8s/helio.yaml`: Deployment (3 replicas, zone spread, read-only root filesystem, probes), Service, HPA, PodDisruptionBudget, Ingress, ConfigMap; `alerts.yaml` holds Prometheus rules on abstention rate, schema violations, latency and spend.
- `deploy/azure/main.bicep`: Container Apps, Azure OpenAI deployments, Azure AI Search, Azure SQL, Redis, Log Analytics, Application Insights, with managed-identity role assignments and no model keys anywhere.
- `.github/workflows/ci.yml`: restore with NuGet vulnerability audit, build with warnings as errors, unit and integration tests, the offline evaluation gate, container build, Trivy scan, optional nightly live evaluation.

Multi-replica deployments must use `Storage__VectorStore=azure-ai-search`; the in-process index is per replica. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Engineering documents

| Document | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System architecture, data flow, file structure, database schema, API contracts, UI architecture |
| [docs/ENGINEERING_REVIEW.md](docs/ENGINEERING_REVIEW.md) | Independent architecture, debugging and performance reviews of this codebase, with what was fixed and what was deliberately deferred |
| [docs/SECURITY_AUDIT.md](docs/SECURITY_AUDIT.md) | Vulnerability report with severities, attack scenarios and fixes |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Infrastructure, CI/CD, monitoring, production checklist |
| [docs/UI.md](docs/UI.md) | Component architecture, props and events, states, accessibility |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Architecture decision records and trade-offs |

## Where this breaks and why

Documented, not hidden:

1. **Paraphrase questions against the deterministic provider.** Two golden cases (q06, q07) fail because the extractive generator is a bag-of-words matcher: when the decisive sentence is several sentences from the best-scoring one, or uses vocabulary the question does not ("BMS", "pack"), it picks the wrong window. A live model reads meaning rather than tokens. The failures are tagged `GenerationFailure`, which tells you the retriever was not at fault (hit rate is 100%).
2. **The in-process index is per replica.** Correct for one node; multi-replica deployments must use Azure AI Search or a re-ingest would be visible on only one pod.
3. **Faithfulness scoring with the lexical judge is trivially 1.0 for extractive answers.** It is a regression detector for CI, not a measurement of a live model. The LLM judge path (`IAnswerJudge`) is where that measurement belongs.
4. **Prompt injection through documents is mitigated, not eliminated.** The prompt instructs the model to treat passage text as data, the citation guard limits damage to retrieved text, and the golden set carries an injection canary. A determined attacker who can write to the corpus can still influence wording; corpus write access is therefore an operator privilege.

## Author

**Edidiong-Abasi Anwanane**
MSc Computer Science, Georgia Institute of Technology
[Portfolio](https://edidionga.github.io) · [LinkedIn](https://www.linkedin.com/in/edidiong-abasi-anwanane/) · [GitHub](https://github.com/EdidiongA)

## License

MIT. See [LICENSE](LICENSE).
