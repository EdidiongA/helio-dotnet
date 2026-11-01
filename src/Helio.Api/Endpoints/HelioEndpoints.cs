using System.ComponentModel.DataAnnotations;
using Helio.Api.Contracts;
using Helio.Api.Security;
using Helio.Application.Answering;
using Helio.Application.Evaluation;
using Helio.Application.Ingestion;
using Helio.Domain;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Helio.Api.Endpoints;

/// <summary>
/// The HTTP surface. Thin by design: validate input, call a handler, map the result. All business
/// rules live in the application layer so they are testable without HTTP.
/// </summary>
public static class HelioEndpoints
{
    public static void MapHelioEndpoints(this WebApplication app)
    {
        // One filter maps caller mistakes to 400 for every endpoint, so no handler can leak a ValidationException as a 500.
        var api = app.MapGroup("/api").AddEndpointFilter<ValidationExceptionFilter>();

        api.MapPost("/ask", AskAsync)
            .WithName("Ask")
            .WithSummary("Answer a question from the knowledge base with citations, or abstain.")
            .RequireAuthorization(Policies.Reader)
            .RequireRateLimiting("ask")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .Produces<AskResponseDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);

        api.MapPost("/documents", IngestAsync)
            .WithName("IngestDocument")
            .WithSummary("Ingest or re-ingest a document. Unchanged content is skipped.")
            .RequireAuthorization(Policies.Operator)
            .RequireRateLimiting("ingest")
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024 * 1024))
            .Produces<IngestResponseDto>(StatusCodes.Status201Created)
            .Produces<IngestResponseDto>()
            .ProducesValidationProblem();

        api.MapGet("/documents", ListDocumentsAsync)
            .WithName("ListDocuments")
            .RequireAuthorization(Policies.Reader)
            .Produces<DocumentDto[]>();

        api.MapGet("/documents/{sourceId}", GetDocumentAsync)
            .WithName("GetDocument")
            .WithSummary("Current version of one source, by its caller-chosen id.")
            .RequireAuthorization(Policies.Reader)
            .Produces<DocumentDto>()
            .Produces(StatusCodes.Status404NotFound);

        api.MapGet("/queries", RecentQueriesAsync)
            .WithName("RecentQueries")
            .WithSummary("Recent query records for operators: what was asked, what was retrieved, what was cited.")
            .RequireAuthorization(Policies.Operator)
            .Produces<QueryRecordDto[]>();

        api.MapPost("/evaluate", EvaluateAsync)
            .WithName("Evaluate")
            .WithSummary("Run a golden set through the live pipeline and return the six-way outcome breakdown.")
            .RequireAuthorization(Policies.Operator)
            .RequireRateLimiting("ingest")
            .WithMetadata(new RequestSizeLimitAttribute(1024 * 1024))
            .Produces<EvalSummaryDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status429TooManyRequests);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        // Status word only: the detailed writer would echo backend error text to an anonymous caller.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = (ctx, report) => ctx.Response.WriteAsync(report.Status.ToString(), ctx.RequestAborted),
        }).AllowAnonymous();
    }

    private static async Task<Ok<AskResponseDto>> AskAsync(
        [FromBody] AskRequestDto body,
        HttpContext http,
        AskQuestionHandler handler,
        HelioMetrics metrics,
        IOptions<SecurityOptions> security,
        CancellationToken cancellationToken)
    {
        var response = await handler.HandleAsync(new AskRequest(body.Question, SecuritySetup.CallerHash(http.User, security.Value)), cancellationToken).ConfigureAwait(false);
        metrics.RecordAsk(response.Answer, response.TotalMs, response.InputTokens, response.CostMicroUsd, response.FromCache);
        http.Response.Headers["X-Helio-Request-Id"] = response.RequestId.ToString();
        return TypedResults.Ok(AskResponseDto.From(response));
    }

    private static async Task<Results<Created<IngestResponseDto>, Ok<IngestResponseDto>>> IngestAsync(
        [FromBody] IngestRequestDto body,
        IngestDocumentHandler handler,
        HelioMetrics metrics,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new IngestRequest(body.SourceId, body.Title, body.Text, body.Metadata), cancellationToken).ConfigureAwait(false);
        metrics.RecordIngest(result.Unchanged);
        var dto = IngestResponseDto.From(result);
        return result.Unchanged ? TypedResults.Ok(dto) : TypedResults.Created($"/api/documents/{Uri.EscapeDataString(result.SourceId)}", dto);
    }

    private static async Task<Ok<DocumentDto[]>> ListDocumentsAsync(IDocumentRepository documents, CancellationToken cancellationToken)
    {
        var list = await documents.ListAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(list.Select(DocumentDto.From).ToArray());
    }

    private static async Task<Results<Ok<DocumentDto>, NotFound>> GetDocumentAsync(string sourceId, IDocumentRepository documents, CancellationToken cancellationToken)
    {
        var document = await documents.FindLatestAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return document is null ? TypedResults.NotFound() : TypedResults.Ok(DocumentDto.From(document));
    }

    private static async Task<Ok<QueryRecordDto[]>> RecentQueriesAsync([FromQuery] int take, IQueryRecordRepository records, CancellationToken cancellationToken)
    {
        var list = await records.RecentAsync(take <= 0 ? 50 : take, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(list.Select(QueryRecordDto.From).ToArray());
    }

    /// <summary>
    /// Budgeted by cases rather than requests: each case is a full pipeline run, so the token-bucket
    /// "evaluate" limiter is charged one token per case and the request is refused when the bucket is short.
    /// A server-side deadline keeps one request from holding a connection indefinitely.
    /// </summary>
    private static async Task<Results<Ok<EvalSummaryDto>, ValidationProblem, StatusCodeHttpResult>> EvaluateAsync(
        [FromBody] EvaluateRequestDto body,
        HttpContext http,
        EvaluationRunner runner,
        IOptions<Application.RagOptions> rag,
        IOptions<SecurityOptions> security,
        CancellationToken cancellationToken)
    {
        var max = rag.Value.MaxEvaluationCasesPerRequest;
        if (body.Cases is null || body.Cases.Length == 0 || body.Cases.Length > max)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cases"] = [$"Provide between 1 and {max} cases; larger sets belong to the offline evaluation CLI."] });
        }

        var invalid = body.Cases.Where(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > rag.Value.MaxQuestionChars).ToArray();
        if (invalid.Length > 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cases"] = [$"{invalid.Length} case(s) have a missing id or a missing or over-long question."] });
        }

        using var lease = await EvaluationBudget.AcquireAsync(http, security.Value, body.Cases.Length, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            http.Response.Headers.RetryAfter = "600";
            return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));

        var cases = body.Cases.Select(c => new GoldenCase(c.Id, c.Question, c.ExpectedKeyphrases ?? [], c.ExpectedSourceId, c.ShouldAbstain, c.Category ?? "general") { ForbiddenPhrases = c.ForbiddenPhrases ?? [], AllowAbstain = c.AllowAbstain }).ToArray();
        var summary = await runner.RunAsync(cases, deadline.Token).ConfigureAwait(false);
        return TypedResults.Ok(EvalSummaryDto.From(summary));
    }
}

/// <summary>Maps <see cref="Application.ValidationException"/> from any handler to a 400 validation problem.</summary>
public sealed class ValidationExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (Application.ValidationException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.Field] = [ex.Message] });
        }
    }
}

/// <summary>Per-operator case budget for evaluation, replenished every ten minutes (in-process, like the other limiters).</summary>
internal static class EvaluationBudget
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.RateLimiting.TokenBucketRateLimiter> Buckets = new(StringComparer.Ordinal);

    public static ValueTask<System.Threading.RateLimiting.RateLimitLease> AcquireAsync(HttpContext http, SecurityOptions options, int cases, CancellationToken cancellationToken)
    {
        var bucket = Buckets.GetOrAdd(SecuritySetup.CallerKey(http), _ => new System.Threading.RateLimiting.TokenBucketRateLimiter(new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(options.EvaluateCasesPerTenMinutes, 1),
            TokensPerPeriod = Math.Max(options.EvaluateCasesPerTenMinutes, 1),
            ReplenishmentPeriod = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
        if (cases > options.EvaluateCasesPerTenMinutes)
        {
            throw new Application.ValidationException("cases", $"At most {options.EvaluateCasesPerTenMinutes} cases may be evaluated per ten minutes through the API.");
        }

        return bucket.AcquireAsync(cases, cancellationToken);
    }
}
