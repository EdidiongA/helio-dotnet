namespace Helio.Domain;

/// <summary>A chunk returned by retrieval, with its relevance score.</summary>
public sealed record RetrievedChunk(Chunk Chunk, double Score, string Retriever);

/// <summary>A citation to a chunk that was actually retrieved for this answer.</summary>
public sealed record Citation(string ChunkId, string SourceId, string Title, string Excerpt);

/// <summary>
/// The validated answer contract. Every answer served by Helio satisfies these invariants:
/// if <see cref="Abstained"/> is false there is at least one citation, and every citation
/// refers to a chunk retrieved for this very request.
/// </summary>
public sealed record Answer
{
    public required string Text { get; init; }

    public required IReadOnlyList<Citation> Citations { get; init; }

    /// <summary>Model-reported confidence in [0, 1]. Treated as advisory, never as a guarantee.</summary>
    public required double Confidence { get; init; }

    public required bool Abstained { get; init; }

    /// <summary>Why the answer was abstained or downgraded, for operators. Null when answered normally.</summary>
    public string? Reason { get; init; }

    /// <summary>Operator-facing detail behind <see cref="Reason"/>, such as the model's own abstention note. Never part of the served text.</summary>
    public string? Detail { get; init; }

    public static Answer Abstain(string reason, string text = "I could not find enough evidence in the knowledge base to answer that reliably.") =>
        new() { Text = text, Citations = [], Confidence = 0, Abstained = true, Reason = reason };
}

/// <summary>What the model produced before the pipeline validated it.</summary>
public sealed record ModelDraft(string Answer, IReadOnlyList<string> CitationChunkIds, double Confidence, bool Abstained);

/// <summary>Outcome of one <c>ask</c> request, persisted for audit and evaluation.</summary>
public sealed class QueryRecord
{
    /// <summary>Time-ordered (version 7) so the clustered primary key appends instead of fragmenting.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Question { get; init; }

    public required string Answer { get; init; }

    public required bool Abstained { get; init; }

    public required double Confidence { get; init; }

    public required IReadOnlyList<string> RetrievedChunkIds { get; init; }

    public required IReadOnlyList<string> CitedChunkIds { get; init; }

    public required string Provider { get; init; }

    public required string ModelVersion { get; init; }

    public required string PromptVersion { get; init; }

    public required int RetrievalMs { get; init; }

    public required int GenerationMs { get; init; }

    public required int TotalMs { get; init; }

    public required int InputTokens { get; init; }

    public required int OutputTokens { get; init; }

    /// <summary>Estimated cost in USD micro-dollars (1e-6 USD), so it is an integer in the database.</summary>
    public required long CostMicroUsd { get; init; }

    public string? Reason { get; init; }

    /// <summary>Pseudonymous caller identifier (hash of the principal), never the raw identity.</summary>
    public string? CallerHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
