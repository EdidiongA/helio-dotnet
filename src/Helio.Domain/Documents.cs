namespace Helio.Domain;

/// <summary>
/// A source document in the knowledge base. Documents are immutable once ingested;
/// re-ingesting the same <see cref="SourceId"/> creates a new <see cref="Version"/>.
/// </summary>
public sealed class Document
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable identifier chosen by the caller (file name, URL, record key).</summary>
    public required string SourceId { get; init; }

    public required string Title { get; init; }

    /// <summary>Monotonic version per <see cref="SourceId"/>. Starts at 1.</summary>
    public int Version { get; init; } = 1;

    /// <summary>SHA-256 of the normalised text; used to skip unchanged re-ingests.</summary>
    public required string ContentHash { get; init; }

    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optional metadata (section, effective date, owner). Kept small and flat.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<Chunk> Chunks { get; init; } = [];

    /// <summary>Number of chunks in this version. Populated on header-only loads where <see cref="Chunks"/> is empty.</summary>
    public int ChunkCount { get; init; }
}

/// <summary>
/// A retrievable unit of text. Chunk identifiers are stable across runs
/// (<c>{sourceId}::v{version}::p{ordinal}</c>) so citations can be audited later.
/// </summary>
public sealed class Chunk
{
    public required string Id { get; init; }

    public required Guid DocumentId { get; init; }

    public required string SourceId { get; init; }

    public required string Title { get; init; }

    public required int Ordinal { get; init; }

    public required string Text { get; init; }

    /// <summary>Approximate token count (words times 1.3), for budget decisions. Counts without allocating.</summary>
    public int ApproximateTokens => (int)Math.Ceiling(CountWords(Text) * 1.3);

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (c == ' ')
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    public static string MakeId(string sourceId, int version, int ordinal) => $"{sourceId}::v{version}::p{ordinal}";
}
