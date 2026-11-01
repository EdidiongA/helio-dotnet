# Deployment

## Infrastructure architecture

```
                       Internet
                          │ TLS (ingress / Container Apps)
                          ▼
              ┌───────────────────────┐      ┌──────────────────┐
              │  Helio.Api replicas   │◄────►│ Redis (answers)  │
              │  stateless, 3..20     │      └──────────────────┘
              └───┬──────────┬────────┘
                  │          │  managed identity (no keys)
         ┌────────▼───┐  ┌───▼──────────────┐    ┌───────────────┐
         │ SQL Server │  │ Azure AI Search  │    │ Azure OpenAI  │
         │ (records)  │  │ (hybrid index)   │    │ chat + embed  │
         └────────────┘  └──────────────────┘    └───────────────┘
                  │
        Log Analytics + Application Insights + Prometheus (/metrics)
```

Three environments are supported by configuration alone:

| | Developer laptop | Single node | Multi-replica production |
|---|---|---|---|
| Database | SQLite file | SQLite on a volume | Azure SQL / SQL Server |
| Vector store | in-process | in-process | Azure AI Search |
| Cache | in-process | in-process | Redis |
| Model | deterministic | deterministic or Azure OpenAI | Azure OpenAI with managed identity |

The in-process index is per replica. Scaling beyond one replica without Azure AI Search would make a re-ingest visible on one pod only; the readiness check reports chunk counts so this is observable, but it must be avoided.

## Deployment workflow

1. Pull request: `ci.yml` restores (NuGet audit fails on known vulnerabilities), builds with warnings as errors, runs 63 tests, runs the offline evaluation gate. All at zero model cost.
2. Merge to `main`: the container image is built, tagged with the commit SHA and `latest`, pushed to GHCR and scanned with Trivy (HIGH and CRITICAL findings are uploaded to code scanning).
3. Nightly: the live evaluation job runs the same golden set against Azure OpenAI and uploads the report, so model or prompt drift is caught before users notice.
4. Promotion: `kubectl apply -f deploy/k8s/` or `az deployment group create -f deploy/azure/main.bicep`. Rolling update with `maxUnavailable: 0`, startup probe on `/health/ready`, PodDisruptionBudget keeps two replicas up.
5. Rollback: redeploy the previous image tag; the database schema is additive and the index is derived, so no data migration is needed to roll back.

## Container

`Dockerfile` publishes onto `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`: no shell, no package manager, non-root user 1654, read-only root filesystem in Kubernetes with `/tmp` as an emptyDir. The compose stack adds Redis and SQL Server for a production-like local run. The image build was validated by running its publish step; building the image itself requires a Docker daemon.

## Monitoring strategy

**Metrics** (OpenTelemetry meter `Helio`, Prometheus at `/metrics`, operator-only):

| Metric | Alert on |
|---|---|
| `helio_answers_total{outcome}` | abstention share > 40% for 15 min |
| `helio_abstentions_total{reason}` | `schema_violation` rate > 0.05/s for 10 min (critical: model output broke the contract) |
| `helio_ask_duration_ms` | p95 > 4 s for 10 min |
| `helio_cost_micro_usd_total` | > 20 USD per hour |
| `helio_ingests_total{result}` | informational |
| ASP.NET Core and runtime instrumentation | 5xx rate, GC pause, thread pool starvation |

**Logs**: Serilog compact JSON to stdout, collected by the platform. Every answer logs request id, abstained flag, reason, retrieved and cited counts and latency; questions are stored in `query_records` (SQL), not in logs.

**Traces**: Application Insights connection string is wired in Bicep; add `AddAspNetCoreInstrumentation` tracing and the Azure Monitor exporter when distributed tracing across the model and search calls is wanted.

**Dashboards**: answer volume and outcome mix, abstention reasons, latency percentiles split by retrieval and generation, cost per hour, cache hit ratio (`outcome="cached"`), chunks indexed (readiness data).

## Reliability

- Readiness gates traffic on database connectivity and an indexed corpus; liveness is cheap and never touches dependencies.
- SQL Server connections retry transient failures (`EnableRetryOnFailure`).
- Model output that fails validation is retried once, then abstained, never served raw.
- Audit persistence failures are logged, never surfaced to users.
- Rate limits are per principal with `Retry-After`; the ingress adds a coarse per-IP limit in front.
- `preStop` sleep plus a 30 s grace period lets in-flight requests finish during rollouts.

## Scaling

- Horizontal: stateless pods under an HPA (CPU 65%); Container Apps scale on concurrent requests.
- Retrieval: Azure AI Search Basic for up to a few million chunks, with replicas for query throughput.
- Model: Azure OpenAI deployment capacity (TPM) is the real ceiling; the answer cache and the no-context short-circuit avoid calls that would not help.
- Database: `query_records` is purged hourly by the built-in retention service once `Retention:QueryRecordRetention` is set (90 days in the manifests). Add an index on `(CallerHash, CreatedAtUtc)` if per-caller reporting is needed.

## Production deployment checklist

- [ ] Operator and reader API keys generated (32+ random bytes), stored in Key Vault or Kubernetes secrets, rotated on a schedule.
- [ ] `Security:AllowAnonymousReads` is false; Entra ID authority and audience configured for interactive users.
- [ ] Azure OpenAI and AI Search accessed by managed identity; `disableLocalAuth` on the OpenAI account.
- [ ] `Storage:VectorStore=azure-ai-search` for any deployment with more than one replica.
- [ ] SQL firewall restricted to the app's outbound addresses or private endpoint (the Bicep `AllowAzureServices` rule is a starting point only).
- [ ] Redis with TLS and password; `abortConnect=false`.
- [ ] TLS at the ingress; HSTS enabled at the edge.
- [ ] Prometheus rules from `deploy/k8s/alerts.yaml` loaded; on-call route for the critical alerts.
- [ ] `Retention:QueryRecordRetention` set (the manifests use 90 days).
- [ ] Golden set reviewed by a subject expert and extended to at least 50 cases; live evaluation job enabled with its secrets.
- [ ] Load test run (see ENGINEERING_REVIEW.md) and HPA thresholds adjusted from the numbers.
- [ ] `deploy/sql/001_chunk_vectors_and_index_cleanup.sql` applied to any database created by an earlier build.
- [ ] Secrets present: operator and reader keys (32+ random characters), `Security:CallerHashPepper`; `Security:JwtAudience` set whenever `JwtAuthority` is.
- [ ] `Proxy:TrustedNetworks` lists the ingress or Container Apps proxy range, so per-address limits see real client addresses.
- [ ] Prometheus `ServiceMonitor` credential is the operator key; confirm `helio_answers_total` appears in Prometheus after the first ask.
- [ ] One Entra token round trip verified against `/api/ask` (reader role) and `/api/documents` (operator role).
- [ ] Image reference is a digest from the CI "push" step, not a tag.
- [ ] Backup and restore of SQL tested; index rebuild from SQL tested (delete the search index, redeploy, confirm readiness chunk count).
