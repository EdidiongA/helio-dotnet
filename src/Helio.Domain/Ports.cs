namespace Helio.Domain;

/// <summary>Splits a document's text into chunks. Implementations must be deterministic.</summary>
public interface IChunker
{
    IReadOnlyList<string> Split(string text);
}

/// <summary>Stores chunks and finds the ones most relevant to a question.</summary>
public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyList<Chunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken);

    Task DeleteBySourceAsync(string sourceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);
}

/// <summary>Chunk embeddings produced by one model, persisted next to the chunk text so a restart never has to pay to re-embed.</summary>
public sealed record EmbeddingSet(string ModelVersion, IReadOnlyList<ReadOnlyMemory<float>> Vectors);

/// <summary>Persists documents and query records.</summary>
public interface IDocumentRepository
{
    /// <summary>Latest version's header only (no chunks). Cheap enough to call on every ingest.</summary>
    Task<Document?> FindLatestAsync(string sourceId, CancellationToken cancellationToken);

    Task AddAsync(Document document, EmbeddingSet? embeddings, CancellationToken cancellationToken);

    Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken);
}

public interface IQueryRecordRepository
{
    Task AddAsync(QueryRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueryRecord>> RecentAsync(int take, CancellationToken cancellationToken);

    /// <summary>Deletes records older than the cut-off and returns how many were removed.</summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>Produces a draft answer from a question and retrieved chunks. Implementations are swappable providers.</summary>
public interface IAnswerGenerator
{
    string ProviderName { get; }

    string ModelVersion { get; }

    Task<GenerationResult> GenerateAsync(string question, IReadOnlyList<RetrievedChunk> context, CancellationToken cancellationToken);

    /// <summary>
    /// Second attempt after <see cref="GenerateAsync"/> returned a draft that did not parse. Implementations
    /// should change something (tell the model what was wrong, raise the output budget) rather than repeat
    /// the identical call, because truncation and malformed JSON are deterministic at low temperature.
    /// </summary>
    Task<GenerationResult> RepairAsync(string question, IReadOnlyList<RetrievedChunk> context, string previousRawText, CancellationToken cancellationToken);
}

/// <summary>Raw model output plus accounting. <see cref="RawText"/> is kept for audit; <see cref="Draft"/> is null when it did not parse.</summary>
public sealed record GenerationResult(string RawText, ModelDraft? Draft, int InputTokens, int OutputTokens, string? ParseError = null);

/// <summary>Embeds text for vector retrieval. Deterministic for a given model.</summary>
public interface ITextEmbedder
{
    string ModelVersion { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
