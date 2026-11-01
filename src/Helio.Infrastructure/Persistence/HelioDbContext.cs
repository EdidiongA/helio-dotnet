using System.Text.Json;
using Helio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Helio.Infrastructure.Persistence;

/// <summary>
/// EF Core model. SQLite for development and tests, SQL Server in production; the schema is the same.
/// Chunk text is stored here as the system of record; the vector store holds a derived copy.
/// </summary>
public sealed class HelioDbContext(DbContextOptions<HelioDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    public DbSet<ChunkRow> Chunks => Set<ChunkRow>();

    public DbSet<QueryRecordRow> QueryRecords => Set<QueryRecordRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRow>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.MetadataJson).HasMaxLength(4000);
            e.HasIndex(x => new { x.SourceId, x.Version }).IsUnique();
            e.HasMany(x => x.Chunks).WithOne().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChunkRow>(e =>
        {
            e.ToTable("chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(260);
            e.Property(x => x.SourceId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Text).IsRequired();
            e.Property(x => x.EmbeddingModel).HasMaxLength(100);
            e.HasIndex(x => x.DocumentId);
        });

        modelBuilder.Entity<QueryRecordRow>(e =>
        {
            e.ToTable("query_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.Question).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Answer).IsRequired();
            e.Property(x => x.RetrievedChunkIdsJson).HasMaxLength(8000).IsRequired();
            e.Property(x => x.CitedChunkIdsJson).HasMaxLength(8000).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(50).IsRequired();
            e.Property(x => x.ModelVersion).HasMaxLength(100).IsRequired();
            e.Property(x => x.PromptVersion).HasMaxLength(100).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(100);
            e.Property(x => x.CallerHash).HasMaxLength(64);
            // CreatedAtUtc serves the "recent" listing and retention purge; a boolean index on Abstained
            // would be low-cardinality and never chosen by the planner, so it is deliberately absent.
            e.HasIndex(x => x.CreatedAtUtc);
        });
    }
}

public sealed class DocumentRow
{
    public Guid Id { get; set; }

    public string SourceId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Version { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Stored as UTC DateTime: SQLite cannot order or compare DateTimeOffset columns, and SQL Server indexes datetime2 efficiently.</summary>
    public DateTime IngestedAtUtc { get; set; }

    public string? MetadataJson { get; set; }

    public List<ChunkRow> Chunks { get; set; } = [];

    public static DocumentRow From(Document d, EmbeddingSet? embeddings = null)
    {
        var withVectors = embeddings is not null && embeddings.Vectors.Count == d.Chunks.Count;
        return new DocumentRow
        {
            Id = d.Id,
            SourceId = d.SourceId,
            Title = d.Title,
            Version = d.Version,
            ContentHash = d.ContentHash,
            IngestedAtUtc = d.IngestedAt.UtcDateTime,
            MetadataJson = d.Metadata.Count == 0 ? null : JsonSerializer.Serialize(d.Metadata),
            Chunks = d.Chunks.Select((c, i) => withVectors ? ChunkRow.From(c, embeddings!.ModelVersion, embeddings.Vectors[i]) : ChunkRow.From(c)).ToList(),
        };
    }

    public Document ToDomain()
    {
        var metadata = MetadataJson is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new Document
        {
            Id = Id,
            SourceId = SourceId,
            Title = Title,
            Version = Version,
            ContentHash = ContentHash,
            IngestedAt = new DateTimeOffset(DateTime.SpecifyKind(IngestedAtUtc, DateTimeKind.Utc)),
            Metadata = metadata,
            Chunks = Chunks.OrderBy(c => c.Ordinal).Select(c => c.ToDomain()).ToArray(),
            ChunkCount = Chunks.Count,
        };
    }

    /// <summary>Header-only projection: no chunk rows are loaded, only their count.</summary>
    public Document ToDomainHeader(int chunkCount)
    {
        var metadata = MetadataJson is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new Document
        {
            Id = Id,
            SourceId = SourceId,
            Title = Title,
            Version = Version,
            ContentHash = ContentHash,
            IngestedAt = new DateTimeOffset(DateTime.SpecifyKind(IngestedAtUtc, DateTimeKind.Utc)),
            Metadata = metadata,
            Chunks = [],
            ChunkCount = chunkCount,
        };
    }
}

public sealed class ChunkRow
{
    public string Id { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public string SourceId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Ordinal { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Which embedding model produced <see cref="Vector"/>; a mismatch at warm-up triggers a re-embed of that chunk only.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Little-endian float32 vector. Persisted so a restart rebuilds the in-memory index without paying for embeddings again.</summary>
    public byte[]? Vector { get; set; }

    public static ChunkRow From(Chunk c, string? embeddingModel = null, ReadOnlyMemory<float> vector = default) => new()
    {
        Id = c.Id,
        DocumentId = c.DocumentId,
        SourceId = c.SourceId,
        Title = c.Title,
        Ordinal = c.Ordinal,
        Text = c.Text,
        EmbeddingModel = vector.IsEmpty ? null : embeddingModel,
        Vector = vector.IsEmpty ? null : VectorCodec.Encode(vector.Span),
    };

    public Chunk ToDomain() => new()
    {
        Id = Id,
        DocumentId = DocumentId,
        SourceId = SourceId,
        Title = Title,
        Ordinal = Ordinal,
        Text = Text,
    };
}

/// <summary>Float32 little-endian encoding for stored vectors; the same bytes decode on every platform .NET runs on.</summary>
public static class VectorCodec
{
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static ReadOnlyMemory<float> Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }
}

public sealed class QueryRecordRow
{
    public Guid Id { get; set; }

    public string Question { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    public bool Abstained { get; set; }

    public double Confidence { get; set; }

    public string RetrievedChunkIdsJson { get; set; } = "[]";

    public string CitedChunkIdsJson { get; set; } = "[]";

    public string Provider { get; set; } = string.Empty;

    public string ModelVersion { get; set; } = string.Empty;

    public string PromptVersion { get; set; } = string.Empty;

    public int RetrievalMs { get; set; }

    public int GenerationMs { get; set; }

    public int TotalMs { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public long CostMicroUsd { get; set; }

    public string? Reason { get; set; }

    public string? CallerHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public static QueryRecordRow From(QueryRecord r) => new()
    {
        Id = r.Id,
        Question = r.Question,
        Answer = r.Answer,
        Abstained = r.Abstained,
        Confidence = r.Confidence,
        RetrievedChunkIdsJson = JsonSerializer.Serialize(r.RetrievedChunkIds),
        CitedChunkIdsJson = JsonSerializer.Serialize(r.CitedChunkIds),
        Provider = r.Provider,
        ModelVersion = r.ModelVersion,
        PromptVersion = r.PromptVersion,
        RetrievalMs = r.RetrievalMs,
        GenerationMs = r.GenerationMs,
        TotalMs = r.TotalMs,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CostMicroUsd = r.CostMicroUsd,
        Reason = r.Reason,
        CallerHash = r.CallerHash,
        CreatedAtUtc = r.CreatedAt.UtcDateTime,
    };

    public QueryRecord ToDomain() => new()
    {
        Id = Id,
        Question = Question,
        Answer = Answer,
        Abstained = Abstained,
        Confidence = Confidence,
        RetrievedChunkIds = JsonSerializer.Deserialize<string[]>(RetrievedChunkIdsJson) ?? [],
        CitedChunkIds = JsonSerializer.Deserialize<string[]>(CitedChunkIdsJson) ?? [],
        Provider = Provider,
        ModelVersion = ModelVersion,
        PromptVersion = PromptVersion,
        RetrievalMs = RetrievalMs,
        GenerationMs = GenerationMs,
        TotalMs = TotalMs,
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        CostMicroUsd = CostMicroUsd,
        Reason = Reason,
        CallerHash = CallerHash,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc)),
    };
}
