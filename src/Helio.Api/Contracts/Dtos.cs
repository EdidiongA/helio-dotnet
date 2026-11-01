using System.ComponentModel.DataAnnotations;
using Helio.Application.Answering;
using Helio.Application.Ingestion;
using Helio.Domain;

namespace Helio.Api.Contracts;

// Wire contracts are separate from domain types so the API can evolve without leaking internals.

public sealed record AskRequestDto([property: Required] string Question);

public sealed record CitationDto(string ChunkId, string SourceId, string Title, string Excerpt);

public sealed record RetrievedDto(string ChunkId, string SourceId, string Title, double Score);

public sealed record AskResponseDto(
    Guid RequestId,
    string Answer,
    bool Abstained,
    string? Reason,
    double Confidence,
    CitationDto[] Citations,
    RetrievedDto[] Retrieved,
    string Provider,
    string ModelVersion,
    string PromptVersion,
    int RetrievalMs,
    int GenerationMs,
    int TotalMs,
    int InputTokens,
    int OutputTokens,
    double EstimatedCostUsd,
    bool FromCache)
{
    public static AskResponseDto From(AskResponse r) => new(
        r.RequestId,
        r.Answer.Text,
        r.Answer.Abstained,
        r.Answer.Reason,
        r.Answer.Confidence,
        r.Answer.Citations.Select(c => new CitationDto(c.ChunkId, c.SourceId, c.Title, c.Excerpt)).ToArray(),
        r.Retrieved.Select(x => new RetrievedDto(x.Chunk.Id, x.Chunk.SourceId, x.Chunk.Title, x.Score)).ToArray(),
        r.Provider,
        r.ModelVersion,
        r.PromptVersion,
        r.RetrievalMs,
        r.GenerationMs,
        r.TotalMs,
        r.InputTokens,
        r.OutputTokens,
        r.CostMicroUsd / 1_000_000.0,
        r.FromCache);
}

public sealed record IngestRequestDto(
    [property: Required, StringLength(200)] string SourceId,
    [property: Required, StringLength(300)] string Title,
    [property: Required] string Text,
    [property: MaxLength(IngestDocumentHandler.MaxMetadataEntries)] Dictionary<string, string>? Metadata);

public sealed record IngestResponseDto(Guid DocumentId, string SourceId, int Version, int ChunkCount, bool Unchanged)
{
    public static IngestResponseDto From(IngestResult r) => new(r.DocumentId, r.SourceId, r.Version, r.ChunkCount, r.Unchanged);
}

public sealed record DocumentDto(Guid Id, string SourceId, string Title, int Version, int ChunkCount, DateTimeOffset IngestedAt, IReadOnlyDictionary<string, string> Metadata)
{
    public static DocumentDto From(Document d) => new(d.Id, d.SourceId, d.Title, d.Version, d.ChunkCount, d.IngestedAt, d.Metadata);
}

public sealed record QueryRecordDto(
    Guid Id,
    string Question,
    string Answer,
    bool Abstained,
    string? Reason,
    double Confidence,
    IReadOnlyList<string> RetrievedChunkIds,
    IReadOnlyList<string> CitedChunkIds,
    string Provider,
    string ModelVersion,
    string PromptVersion,
    int TotalMs,
    int InputTokens,
    int OutputTokens,
    double EstimatedCostUsd,
    DateTimeOffset CreatedAt)
{
    public static QueryRecordDto From(QueryRecord r) => new(
        r.Id, r.Question, r.Answer, r.Abstained, r.Reason, r.Confidence, r.RetrievedChunkIds, r.CitedChunkIds,
        r.Provider, r.ModelVersion, r.PromptVersion, r.TotalMs, r.InputTokens, r.OutputTokens, r.CostMicroUsd / 1_000_000.0, r.CreatedAt);
}

public sealed record GoldenCaseDto(
    [property: Required] string Id,
    [property: Required] string Question,
    string[]? ExpectedKeyphrases,
    string? ExpectedSourceId,
    bool ShouldAbstain,
    string? Category,
    string[]? ForbiddenPhrases = null,
    bool AllowAbstain = false);

public sealed record EvaluateRequestDto(GoldenCaseDto[] Cases);

public sealed record EvalCaseResultDto(string CaseId, string Category, string Outcome, bool RetrievalHit, double Faithfulness, double AnswerRelevance, int LatencyMs, string Answer, IReadOnlyList<string> CitedChunkIds);

public sealed record EvalSummaryDto(
    int Cases,
    double Accuracy,
    double RetrievalHitRate,
    double MeanFaithfulness,
    double MeanAnswerRelevance,
    int FalseAnswers,
    double AbstentionRate,
    int P50LatencyMs,
    int P95LatencyMs,
    Dictionary<string, int> Outcomes,
    EvalCaseResultDto[] Results)
{
    public static EvalSummaryDto From(EvalSummary s) => new(
        s.Cases, s.Accuracy, s.RetrievalHitRate, s.MeanFaithfulness, s.MeanAnswerRelevance, s.FalseAnswers, s.AbstentionRate, s.P50LatencyMs, s.P95LatencyMs,
        s.Outcomes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value, StringComparer.Ordinal),
        s.Results.Select(r => new EvalCaseResultDto(r.CaseId, r.Category, r.Outcome.ToString(), r.RetrievalHit, r.Faithfulness, r.AnswerRelevance, r.LatencyMs, r.AnswerText, r.CitedChunkIds)).ToArray());
}
