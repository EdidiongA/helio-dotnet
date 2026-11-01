using System.Diagnostics;
using Helio.Application.Text;
using Helio.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helio.Application.Answering;

/// <summary>Input to the ask pipeline. <paramref name="CallerHash"/> is a pseudonymous principal id, never a raw identity.</summary>
/// <param name="BypassCache">Skip the answer cache for this request (evaluation runs must measure the live pipeline).</param>
public sealed record AskRequest(string Question, string? CallerHash = null, bool BypassCache = false);

/// <summary>The served answer plus the trace an operator needs to understand it.</summary>
public sealed record AskResponse(
    Guid RequestId,
    Answer Answer,
    IReadOnlyList<RetrievedChunk> Retrieved,
    string Provider,
    string ModelVersion,
    string PromptVersion,
    int RetrievalMs,
    int GenerationMs,
    int TotalMs,
    int InputTokens,
    int OutputTokens,
    long CostMicroUsd,
    bool FromCache);

/// <summary>
/// The retrieval-augmented answering pipeline:
/// question → embed → hybrid retrieve → relative-score filter → generate → parse (one repair retry)
/// → citation guard → persist record → respond. Every stage is timed and every outcome is recorded,
/// including cache hits, which get their own request id and zero token accounting.
/// </summary>
public sealed class AskQuestionHandler(
    ITextEmbedder embedder,
    IVectorStore vectorStore,
    IAnswerGenerator generator,
    IQueryRecordRepository records,
    IAnswerCache cache,
    IIndexGeneration indexGeneration,
    ICostModel costModel,
    IOptions<RagOptions> options,
    ILogger<AskQuestionHandler> logger)
{
    public const string CacheProviderName = "cache";

    private readonly RagOptions _options = options.Value;

    public async Task<AskResponse> HandleAsync(AskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var question = ValidateQuestion(request.Question);
        var total = Stopwatch.StartNew();

        var useCache = _options.AnswerCacheTtl > TimeSpan.Zero && !request.BypassCache;
        string? cacheKey = null;
        if (useCache)
        {
            cacheKey = await CacheKeyAsync(question, cancellationToken).ConfigureAwait(false);
            var cached = await cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                Log.CacheHit(logger, cached.RequestId);
                return await ServeFromCacheAsync(request, question, cached, total, cancellationToken).ConfigureAwait(false);
            }
        }

        // Model calls get a deadline of their own so a hung upstream becomes an abstention, not a stuck request.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.GenerationTimeout);
        var modelToken = deadline.Token;

        var requestId = Guid.CreateVersion7();
        var retrievalWatch = Stopwatch.StartNew();
        RetrievedChunk[] retrieved;
        Answer answer;
        GenerationResult generation = new(string.Empty, null, 0, 0);
        var generationWatch = new Stopwatch();

        try
        {
            // 1. Retrieve.
            var queryEmbedding = (await embedder.EmbedAsync([question], modelToken).ConfigureAwait(false))[0];
            var candidates = await vectorStore.SearchAsync(question, queryEmbedding, _options.CandidateK, modelToken).ConfigureAwait(false);
            retrieved = FilterAndTrim(candidates);
            retrievalWatch.Stop();
            generationWatch.Start();

            if (retrieved.Length == 0)
            {
                // Nothing relevant: do not spend model tokens on an answer we would have to abstain from anyway.
                answer = Answer.Abstain("no_relevant_context");
            }
            else
            {
                // 2. Generate, with one repair attempt if the reply does not parse.
                generation = await generator.GenerateAsync(question, retrieved, modelToken).ConfigureAwait(false);
                if (generation.Draft is null)
                {
                    Log.DraftParseFailed(logger, generation.ParseError);
                    var repair = await generator.RepairAsync(question, retrieved, generation.RawText, modelToken).ConfigureAwait(false);
                    generation = repair with
                    {
                        InputTokens = repair.InputTokens + generation.InputTokens,
                        OutputTokens = repair.OutputTokens + generation.OutputTokens,
                    };
                }

                // 3. Validate and guard.
                answer = generation.Draft is null
                    ? Answer.Abstain("schema_violation")
                    : CitationGuard.Apply(generation.Draft, retrieved);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline fired, not the caller. Serve an honest abstention and record it.
            Log.UpstreamTimeout(logger, requestId, (int)_options.GenerationTimeout.TotalMilliseconds);
            retrieved = [];
            answer = Answer.Abstain("upstream_timeout", "The knowledge base did not respond in time. Please try again.");
        }

        retrievalWatch.Stop();
        generationWatch.Stop();
        total.Stop();

        var cost = costModel.EstimateMicroUsd(generator.ModelVersion, generation.InputTokens, generation.OutputTokens);
        var response = new AskResponse(
            requestId,
            answer,
            retrieved,
            generator.ProviderName,
            generator.ModelVersion,
            _options.PromptVersion,
            (int)retrievalWatch.ElapsedMilliseconds,
            (int)generationWatch.ElapsedMilliseconds,
            (int)total.ElapsedMilliseconds,
            generation.InputTokens,
            generation.OutputTokens,
            cost,
            FromCache: false);

        // 4. Persist the audit record and populate the cache. Neither may fail the user's request.
        await PersistAsync(request, question, response, cancellationToken).ConfigureAwait(false);
        if (useCache && !answer.Abstained)
        {
            await cache.SetAsync(cacheKey!, response, _options.AnswerCacheTtl, cancellationToken).ConfigureAwait(false);
        }

        Log.Answered(logger, requestId, answer.Abstained, answer.Reason, retrieved.Length, answer.Citations.Count, response.TotalMs);
        return response;
    }

    private async Task<AskResponse> ServeFromCacheAsync(AskRequest request, string question, AskResponse cached, Stopwatch total, CancellationToken cancellationToken)
    {
        total.Stop();
        var hit = cached with
        {
            RequestId = Guid.CreateVersion7(),
            FromCache = true,
            RetrievalMs = 0,
            GenerationMs = 0,
            TotalMs = (int)total.ElapsedMilliseconds,
            InputTokens = 0,
            OutputTokens = 0,
            CostMicroUsd = 0,
        };
        await PersistAsync(request, question, hit with { Provider = CacheProviderName }, cancellationToken).ConfigureAwait(false);
        return hit;
    }

    private async Task PersistAsync(AskRequest request, string question, AskResponse response, CancellationToken cancellationToken)
    {
        try
        {
            await records.AddAsync(ToRecord(request, question, response), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.PersistFailed(logger, ex, response.RequestId);
        }
    }

    private string ValidateQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ValidationException("question", "A question is required.");
        }

        var trimmed = TextNormalizer.NormalizeUnicode(question.Trim());
        if (trimmed.Length > _options.MaxQuestionChars)
        {
            throw new ValidationException("question", $"Questions are limited to {_options.MaxQuestionChars} characters.");
        }

        return trimmed;
    }

    private RetrievedChunk[] FilterAndTrim(IReadOnlyList<RetrievedChunk> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var best = candidates[0].Score;
        if (best <= 0)
        {
            return [];
        }

        var floor = best * _options.MinRelativeScore;
        return candidates.Where(c => c.Score >= floor).Take(_options.TopK).ToArray();
    }

    /// <summary>
    /// The key encodes everything that changes the correct answer: the question, the index generation,
    /// the prompt, both model versions and the retrieval settings. A re-ingest or a deploy that changes
    /// any of these therefore misses the cache instead of serving superseded citations.
    /// </summary>
    private async Task<string> CacheKeyAsync(string question, CancellationToken cancellationToken)
    {
        var generation = await indexGeneration.CurrentAsync(cancellationToken).ConfigureAwait(false);
        var material = string.Join('|',
            generation,
            _options.PromptVersion,
            generator.ModelVersion,
            embedder.ModelVersion,
            _options.TopK,
            _options.CandidateK,
            _options.MinRelativeScore,
            question.ToLowerInvariant());
        return "answer:" + TextNormalizer.Sha256Hex(material);
    }

    private static QueryRecord ToRecord(AskRequest request, string question, AskResponse response) => new()
    {
        Id = response.RequestId,
        Question = question,
        Answer = response.Answer.Text,
        Abstained = response.Answer.Abstained,
        Confidence = response.Answer.Confidence,
        RetrievedChunkIds = response.Retrieved.Select(r => r.Chunk.Id).ToArray(),
        CitedChunkIds = response.Answer.Citations.Select(c => c.ChunkId).ToArray(),
        Provider = response.Provider,
        ModelVersion = response.ModelVersion,
        PromptVersion = response.PromptVersion,
        RetrievalMs = response.RetrievalMs,
        GenerationMs = response.GenerationMs,
        TotalMs = response.TotalMs,
        InputTokens = response.InputTokens,
        OutputTokens = response.OutputTokens,
        CostMicroUsd = response.CostMicroUsd,
        Reason = response.Answer.Reason,
        CallerHash = request.CallerHash,
    };
}

internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Answer cache hit for request {RequestId}")]
    public static partial void CacheHit(ILogger logger, Guid requestId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Draft parse failed ({Error}); attempting one repair")]
    public static partial void DraftParseFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Failed to persist query record {RequestId}")]
    public static partial void PersistFailed(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Answered {RequestId} abstained={Abstained} reason={Reason} retrieved={Retrieved} cited={Cited} in {TotalMs} ms")]
    public static partial void Answered(ILogger logger, Guid requestId, bool abstained, string? reason, int retrieved, int cited, int totalMs);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Request {RequestId} exceeded the {TimeoutMs} ms generation deadline; abstaining")]
    public static partial void UpstreamTimeout(ILogger logger, Guid requestId, int timeoutMs);
}
