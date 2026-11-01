# Security audit

An independent audit was run against the first complete build, reading every file in the API, security, infrastructure, application and domain layers plus the Dockerfile, compose file, Kubernetes manifests, Bicep template and CI workflow. Severity reflects exploitability in the documented production deployments, not the local developer setup. This document lists each finding with its attack scenario, the fix applied, and how the fix is verified. Items that remain open are marked and explained.

## Summary

| Severity | Found | Fixed | Open |
|---|---|---|---|
| High | 2 | 2 | 0 |
| Medium | 9 | 9 | 0 |
| Low | 13 | 11 | 2 |
| Informational | 10 | 8 | 2 |

Verification after the fixes: 79 automated tests pass (61 unit, 18 integration), including new tests for metadata limits, evaluation validation, bearer-token scraping, exact metric names, abstention text, prompt fencing, and cache invalidation on re-ingest.

## High

### H-1. Development configuration shipped inside the production image

**Scenario.** `appsettings.Development.json` (anonymous reads on, a known operator key) was copied by `dotnet publish` into every image. Any deployment with `ASPNETCORE_ENVIRONMENT=Development` set, a common "turn on verbose errors for a minute" change, granted unauthenticated reads and full operator access through a public key: poison the knowledge base, read every user's questions, burn model spend.

**Fix.** `CopyToPublishDirectory="Never"` on the file; the development key is a long non-placeholder value; start-up validators refuse, in Production, anonymous reads, any key shorter than 32 characters, a key containing "change-me" (in every environment), and a configuration with no credential source at all. CI publishes the API and asserts the development file is absent.

**Verified by.** The publish smoke step in `.github/workflows/ci.yml`; `SecuritySetup.AddHelioSecurity` validators run under `ValidateOnStart`.

### H-2. JWT audience validation silently disabled

**Scenario.** With `JwtAuthority` set and `JwtAudience` blank (the shipped default), any token signed by the tenant for any application was accepted. A user who can create an app registration defines a role named `Helio.Operator`, issues themselves a token for their own app, and is an operator.

**Fix.** `JwtAudience` is mandatory whenever an authority is configured (start-up validator), `ValidateAudience` is always true with `ValidAudiences = [audience, "api://" + audience]`, inbound claim mapping is off so `roles` and `oid` are read under their wire names, and a separate `Helio.Reader` app role exists so ordinary users no longer need operator rights to ask a question (M-2).

**Verified by.** Validator; configuration follows the documented Microsoft pattern. No Entra tenant was available in the build environment, so a live token round trip remains on the deployment checklist.

## Medium

### M-1. `/api/evaluate` as a cost amplifier

**Scenario.** 500 cases per request under a 10 requests-per-minute policy allowed 10,000 model calls per minute per operator key, sequentially inside one connection, bypassing the `ask` limiter.

**Fix.** 50 cases per request (`Rag:MaxEvaluationCasesPerRequest`), a per-operator token bucket of 200 cases per ten minutes (`Security:EvaluateCasesPerTenMinutes`, charged one token per case), a three-minute server-side deadline, and 400 for malformed cases. Larger sets run through the offline CLI.

**Verified by.** `Evaluate_ValidatesCases_AndRunsAgainstTheLivePipeline`.

### M-2. No Entra reader role

Fixed with H-2: `JwtReaderRole` maps to the reader policy; the operator role still implies reader.

### M-3. Anonymous callers shared one rate-limit bucket and one audit pseudonym

**Scenario.** With anonymous reads enabled, one client exhausts the ask budget for every anonymous user on the pod, and every anonymous question carries the same caller hash.

**Fix.** Anonymous callers are keyed by client address; `X-Forwarded-For` is honoured only from networks listed in `Proxy:TrustedNetworks` (ingress controller CIDRs), with `ForwardLimit = 1`; a global per-address limiter (`Security:UnauthenticatedPermitPerMinute`, default 30) throttles requests with no valid principal, which also bounds key guessing (L-9).

### M-4. Mistyped provider or store names fell back silently

**Scenario.** `Ai:Provider=AzureOpenAI` (wrong case) selected the offline deterministic provider; readiness stayed green; answers were non-semantic extracts.

**Fix.** `RegularExpression` validation on `Provider`, `Database` and `VectorStore`; Production refuses the deterministic provider unless `Ai:AllowDeterministicInProduction=true` is set deliberately; the memory store is refused when Redis is configured unless `Storage:AllowMemoryStoreWithRedis=true` (single replica).

### M-5. Azure AI Search batch and delete limits

**Scenario.** A large manual produced more than 1,000 chunks: the old vectors were deleted, the single upsert batch was rejected, and the source vanished from search; or stale chunks from a previous version remained citeable.

**Fix.** Uploads in batches of 500 with per-document success checks; paged deletes until the filter returns nothing; a 4,000-chunk cap at ingest. Compile-verified; no live search service in the build environment.

### M-6. Application connected to SQL as the server administrator; firewall open to all of Azure

**Fix (Bicep).** Entra-only SQL authentication with a group administrator and no SQL password anywhere in the template; the app connects with `Authentication=Active Directory Default` as a contained user created once per environment with `db_ddladmin`, `db_datareader` and `db_datawriter`; the `0.0.0.0` rule is replaced by the Container Apps environment's egress address. Compose runs the app as a least-privilege login created by an init job rather than as `sa`.

### M-7. Metrics unscrapable, so every alert was dead

**Fix.** The API key handler accepts the operator key as `Authorization: Bearer` (a token without dots is treated as a key, so JWTs are unaffected); the manifests use a Prometheus Operator `ServiceMonitor` with `authorization.credentials` from the secret; the latency alert uses the real series name `helio_ask_duration_milliseconds_bucket`; the Ingress returns 404 for `/metrics`, `/health/ready` and `/openapi` so they are reachable only inside the cluster.

**Verified by.** `Metrics_RequireOperator` asserts both the exact series names and the bearer path.

### M-8. Kubernetes manifest claimed workload identity without configuring it

**Fix.** A `ServiceAccount` annotated with the identity's client id, the `azure.workload.identity/use` label on the pod template, `serviceAccountName` and `automountServiceAccountToken: true` (required for the projected token).

### M-9. Image pushed before the scan; scan could not fail the pipeline; deploy by mutable tag

**Fix.** Build locally, scan with Trivy at `exit-code: 1` for HIGH and CRITICAL, then push only the clean image by commit SHA with SBOM and provenance attestations; the digest is printed and uploaded; the Deployment and Bicep take a digest, `imagePullPolicy: Always`. Workflow permissions are least-privilege per job. Dependabot covers NuGet, Docker and Actions.

## Low

| Id | Finding | Status |
|---|---|---|
| L-1 | No application-level request body limits. | Fixed: Kestrel 4 MB globally; `RequestSizeLimit` metadata of 16 KB on ask, 4 MB on ingest, 1 MB on evaluate. |
| L-2 | Decorative DataAnnotations; evaluate returned 500 on bad input; oversized metadata failed after vectors were written. | Fixed: shared `ValidationExceptionFilter`; metadata validated before any work; evaluate cases validated. |
| L-3 | Prompt fields not delimited; passages could forge structure. | Fixed: every untrusted field is fenced in `<passage>` or `<question>` elements with `<` escaped; the system prompt names the elements. Test `BuildUserMessage_FencesUntrustedFieldsSoPassagesCannotForgeStructure`. |
| L-4 | Readiness endpoint anonymous, uncached, and echoing backend error text. | Fixed: five-second result cache; response is the status word only; never echoes provider errors; excluded at the Ingress. |
| L-5 | Cache hits unaudited; entries survived re-ingestion. | Fixed: see ENGINEERING_REVIEW B3 and B4. |
| L-6 | Unsalted SHA-256 caller pseudonym; no retention. | Fixed: HMAC-SHA256 with `Security:CallerHashPepper` (required in Production); `Retention:QueryRecordRetention` with an hourly purge. |
| L-7 | Raw question sent as Simple-syntax query to Azure AI Search. | Fixed: operators escaped; `SearchMode.Any`; `Select` excludes the vector. Test `EscapeSimpleQuery_NeutralisesOperators`. |
| L-8 | Possible quadratic backtracking in the sentence splitter on long whitespace runs. | Fixed: whitespace runs collapsed before the boundary regex runs. |
| L-9 | No throttle on failed authentication. | Fixed with M-3 (global per-address limiter for unauthenticated requests). |
| L-10 | Floating base-image tags and unpinned actions. | Open (partial): Dependabot added for all three ecosystems; actions pinned to tags. Pinning base images and actions to digests requires looking the digests up at release time and is on the deployment checklist. |
| L-11 | Prerelease packages in the production graph. | Open: `Azure.AI.OpenAI 2.9.0-beta.1` and the Prometheus exporter beta remain because the stable lines do not yet target the same `Microsoft.Extensions.AI` surface; NuGet audit is explicit in CI (`dotnet list package --vulnerable --include-transitive`). |
| L-12 | Pod hardening gaps. | Fixed: default-deny `NetworkPolicy` (ingress from the ingress and monitoring namespaces, egress to DNS, 443, 1433, 6380); `disableLocalAuth: true` on the search service; service-account token mounted only because workload identity needs it. |
| L-13 | Compose shipped insecure defaults. | Fixed: every secret is a required variable; API bound to 127.0.0.1; `Encrypt=true`; Redis `requirepass`; least-privilege SQL login; 30-day retention. |

## Informational

- `MapOpenApi` is now Development-only; the console footer no longer links to it.
- `UseHsts` is on outside Development; CSP gained `object-src 'none'`.
- `Location` on 201 now points at a route that exists (`GET /api/documents/{sourceId}`).
- `MapInboundClaims = false` with `RoleClaimType = "roles"` and `NameClaimType = "oid"`; the caller pseudonym prefers `oid`.
- The Bicep comment claiming Key Vault was corrected; secrets are Container Apps secrets, with Key Vault references listed as the next step.
- The CI live-evaluation job has a nightly schedule and uses OIDC federation rather than a stored model key.
- Rate-limit state is per replica (documented in the options and ADR-013); a Redis-backed limiter is the step to take when the number must be exact.
- `GET /api/documents` and `GET /api/queries` have no dedicated limiter; they are authenticated reads of small result sets and fall under the global unauthenticated throttle when no credential is presented. Open by choice.
- `DefaultAzureCredential` is retained: on AKS it resolves the projected workload-identity token first; an explicit `ChainedTokenCredential` is a one-line change if a deployment needs to exclude developer credentials. Open by choice.

## Controls verified as correctly implemented (unchanged)

Constant-time API key comparison with fingerprint-only logging; every business endpoint behind a policy; `/metrics` operator-only; sliding and fixed window limits with `Retry-After`; question and ingest validation on the server; `take` clamped; OData filter built with `SearchFilter.Create`; all database access through EF Core LINQ with declared column lengths; citation guard; adversarial golden case with a zero-false-answer gate; the console escapes every API string before `innerHTML` with no inline handlers; CSP with `frame-ancestors 'none'`; no CORS policy and a header-borne key (no CSRF vector); generic ProblemDetails outside Development; structured logs that omit questions, answers, keys and tokens; managed identity for OpenAI and Search; TLS 1.2 minimums; chiseled non-root image; pod security context with a read-only root filesystem and all capabilities dropped; secrets referenced never embedded; local database excluded from git and the image.

## Attack scenarios re-tested after the fixes

1. **Poisoned passage asks the model to abstain with a planted figure.** The abstention text served is fixed; the planted figure can only reach `Answer.Detail`, which the console does not render to readers. Golden case q24 and unit test `Apply_NeverServesInjectedTextThroughAnAbstention`.
2. **Passage that forges a new passage header or a question.** Angle brackets are escaped and fields are fenced; the forged elements appear as literal text. Unit test on `PromptBuilder`.
3. **Re-ingest a corrected document, then ask a question cached under the old version.** The generation marker changes the key; the next answer cites the new version. Integration test `Reingest_InvalidatesCachedAnswers`.
4. **Operator posts 25 metadata entries or an evaluation case with an empty question.** 400 before any vector is written or any model call is made. Integration tests.
5. **Unauthenticated client guesses keys at speed.** 30 requests per minute per address at the application layer before the ingress limit is reached.
6. **Prometheus scrapes with the operator key as a bearer token; a reader tries the same.** 200 and 403 respectively. Integration test.
