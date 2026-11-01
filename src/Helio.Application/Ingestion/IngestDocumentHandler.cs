using System.Collections.Concurrent;
using System.Text.Json;
using Helio.Application.Text;
using Helio.Domain;
using Microsoft.Extensions.Logging;

namespace Helio.Application.Ingestion;

public sealed record IngestRequest(string SourceId, string Title, string Text, IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record IngestResult(Guid DocumentId, string SourceId, int Version, int ChunkCount, bool Unchanged);

/// <summary>
/// Ingestion pipeline: normalise → hash → skip if unchanged → chunk → embed → replace vectors → persist document → bump generation.
/// Ingests for one source are serialised in-process so two writers can never interleave versions, and the
/// commit sequence (vector swap, row insert) runs on a non-cancellable token: once embeddings have been paid
/// for, a disconnecting client must not leave the index and the database disagreeing. Vectors are written
/// before the document row so a crash leaves at worst an orphaned vector set that the next ingest replaces;
/// the reverse order could leave a document that retrieval cannot see until restart.
/// </summary>
public sealed class IngestDocumentHandler(
    IChunker chunker,
    ITextEmbedder embedder,
    IVectorStore vectorStore,
    IDocumentRepository documents,
    IIndexGeneration indexGeneration,
    ILogger<IngestDocumentHandler> logger)
{
    public const int MaxTextChars = 2_000_000;
    public const int MaxChunks = 4_000;
    public const int MaxMetadataEntries = 20;
    public const int MaxMetadataJsonChars = 3_500;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SourceLocks = new(StringComparer.Ordinal);

    public async Task<IngestResult> HandleAsync(IngestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var gate = SourceLocks.GetOrAdd(request.SourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleLockedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IngestResult> HandleLockedAsync(IngestRequest request, CancellationToken cancellationToken)
    {
        var title = request.Title.Trim();
        var text = TextNormalizer.NormalizeLineEndings(TextNormalizer.NormalizeUnicode(request.Text)).Trim();
        var metadata = request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
        // The hash covers everything a version stores, so a title or metadata change is a real new version
        // rather than a silently dropped update.
        var hash = TextNormalizer.Sha256Hex(string.Concat(title, "\n", CanonicalMetadata(metadata), "\n", text));

        var existing = await documents.FindLatestAsync(request.SourceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.ContentHash, hash, StringComparison.Ordinal))
        {
            Log.SkippedUnchanged(logger, existing.SourceId, existing.Version);
            return new IngestResult(existing.Id, existing.SourceId, existing.Version, existing.ChunkCount, Unchanged: true);
        }

        var version = (existing?.Version ?? 0) + 1;
        var pieces = chunker.Split(text);
        if (pieces.Count == 0)
        {
            throw new ValidationException("text", "The document produced no chunks after normalisation.");
        }

        if (pieces.Count > MaxChunks)
        {
            throw new ValidationException("text", $"The document produced {pieces.Count} chunks; the limit is {MaxChunks}. Split it into sections.");
        }

        var documentId = Guid.NewGuid();
        var chunks = pieces.Select((piece, i) => new Chunk
        {
            Id = Chunk.MakeId(request.SourceId, version, i),
            DocumentId = documentId,
            SourceId = request.SourceId,
            Title = title,
            Ordinal = i,
            Text = piece,
        }).ToArray();

        // The expensive, safely abortable step keeps the caller's token.
        var embeddings = await embedder.EmbedAsync(chunks.Select(c => c.Text).ToArray(), cancellationToken).ConfigureAwait(false);

        // From here on the write sequence completes even if the client has gone away.
        var commit = CancellationToken.None;
        await vectorStore.DeleteBySourceAsync(request.SourceId, commit).ConfigureAwait(false);
        await vectorStore.UpsertAsync(chunks, embeddings, commit).ConfigureAwait(false);

        var document = new Document
        {
            Id = documentId,
            SourceId = request.SourceId,
            Title = title,
            Version = version,
            ContentHash = hash,
            Metadata = metadata,
            Chunks = chunks,
            ChunkCount = chunks.Length,
        };
        await documents.AddAsync(document, new EmbeddingSet(embedder.ModelVersion, embeddings), commit).ConfigureAwait(false);
        await indexGeneration.BumpAsync(commit).ConfigureAwait(false);

        Log.Ingested(logger, document.SourceId, version, chunks.Length);
        return new IngestResult(documentId, document.SourceId, version, chunks.Length, Unchanged: false);
    }

    internal static string CanonicalMetadata(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Count == 0 ? string.Empty : JsonSerializer.Serialize(metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

    private static void Validate(IngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceId) || request.SourceId.Length > 200 || request.SourceId.Contains("::", StringComparison.Ordinal))
        {
            throw new ValidationException("sourceId", "sourceId is required, at most 200 characters, and must not contain '::'.");
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 300)
        {
            throw new ValidationException("title", "title is required and at most 300 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ValidationException("text", "text is required.");
        }

        if (request.Text.Length > MaxTextChars)
        {
            throw new ValidationException("text", $"text is limited to {MaxTextChars:N0} characters per document.");
        }

        if (request.Metadata is { } metadata)
        {
            if (metadata.Count > MaxMetadataEntries)
            {
                throw new ValidationException("metadata", $"metadata is limited to {MaxMetadataEntries} entries.");
            }

            if (metadata.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 100 || kv.Value is null || kv.Value.Length > 1000))
            {
                throw new ValidationException("metadata", "metadata keys must be 1 to 100 characters and values at most 1000 characters.");
            }

            if (CanonicalMetadata(metadata).Length > MaxMetadataJsonChars)
            {
                throw new ValidationException("metadata", $"metadata is limited to {MaxMetadataJsonChars:N0} characters when serialised.");
            }
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Skipping unchanged document {SourceId} v{Version}")]
    public static partial void SkippedUnchanged(ILogger logger, string sourceId, int version);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Ingested {SourceId} v{Version}: {Chunks} chunks")]
    public static partial void Ingested(ILogger logger, string sourceId, int version, int chunks);
}
