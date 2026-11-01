namespace Helio.Domain;

/// <summary>One question in the golden set, written by a subject expert.</summary>
public sealed record GoldenCase(
    string Id,
    string Question,
    IReadOnlyList<string> ExpectedKeyphrases,
    string? ExpectedSourceId,
    bool ShouldAbstain,
    string Category)
{
    /// <summary>Phrases that must never appear in a served answer (for example a value injected by a prompt attack). Any hit is a FalseAnswer.</summary>
    public IReadOnlyList<string> ForbiddenPhrases { get; init; } = [];

    /// <summary>When true, an abstention is acceptable even though an answer exists (used for adversarial cases where refusing is a safe outcome).</summary>
    public bool AllowAbstain { get; init; }
}

/// <summary>
/// Six-way outcome taxonomy. The point of evaluation is not an accuracy number but knowing
/// which component failed, so every case lands in exactly one of these buckets.
/// </summary>
public enum EvalOutcome
{
    Correct,
    CorrectAbstention,
    RetrievalFailure,
    GenerationFailure,
    CitationMismatch,
    FalseAnswer,
}

public sealed record EvalCaseResult(
    string CaseId,
    string Category,
    EvalOutcome Outcome,
    bool RetrievalHit,
    double Faithfulness,
    double AnswerRelevance,
    int LatencyMs,
    string AnswerText,
    IReadOnlyList<string> CitedChunkIds);

public sealed record EvalSummary(
    int Cases,
    double Accuracy,
    double RetrievalHitRate,
    double MeanFaithfulness,
    double MeanAnswerRelevance,
    int FalseAnswers,
    double AbstentionRate,
    int P50LatencyMs,
    int P95LatencyMs,
    IReadOnlyDictionary<EvalOutcome, int> Outcomes,
    IReadOnlyList<EvalCaseResult> Results);
