using System.Numerics.Tensors;
using Helio.Application.Text;
using Helio.Domain;

namespace Helio.Infrastructure.Retrieval;

/// <summary>
/// Hybrid retrieval over an in-process index: BM25 over tokens plus cosine similarity over embeddings,
/// fused with reciprocal rank fusion (RRF). Keyword search matters for reference material where users
/// quote exact terms and part numbers; vectors catch paraphrases. RRF needs no score calibration
/// between the two, which is why it is the fusion method of choice in Azure AI Search as well.
/// </summary>
/// <remarks>
/// Concurrency model: writers take <see cref="_writeLock"/> and publish a brand-new immutable
/// <see cref="Snapshot"/> (entries, document frequencies, average length) with one volatile store.
/// Readers grab the current snapshot once and never touch shared state again, so a search can
/// never observe a half-applied re-ingest or look up an id that a concurrent delete removed.
/// Statistics are maintained incrementally, so an upsert costs O(chunk tokens), not O(index).
/// Suitable for corpora up to a few hundred thousand chunks on one node; beyond that, use the
/// Azure AI Search store, which exposes the same interface.
/// </remarks>
public sealed class InMemoryHybridStore : IVectorStore
{
    private const double Bm25K1 = 1.5;
    private const double Bm25B = 0.75;
    private const int RrfK = 60;

    private readonly object _writeLock = new();
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public Task UpsertAsync(IReadOnlyList<Chunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddings);
        if (chunks.Count != embeddings.Count)
        {
            throw new ArgumentException("One embedding is required per chunk.", nameof(embeddings));
        }

        if (chunks.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Tokenise outside the lock; only the publish step is serialised.
        var incoming = new Entry[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            incoming[i] = Entry.Create(chunks[i], embeddings[i]);
        }

        lock (_writeLock)
        {
            var current = _snapshot;
            var entries = new Dictionary<string, Entry>(current.Entries, StringComparer.Ordinal);
            var df = new Dictionary<string, int>(current.DocumentFrequency, StringComparer.Ordinal);
            var totalLength = current.TotalLength;

            foreach (var entry in incoming)
            {
                if (entries.TryGetValue(entry.Chunk.Id, out var previous))
                {
                    Remove(previous, df, ref totalLength);
                }

                entries[entry.Chunk.Id] = entry;
                Add(entry, df, ref totalLength);
            }

            _snapshot = new Snapshot(entries, df, totalLength);
        }

        return Task.CompletedTask;
    }

    public Task DeleteBySourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        lock (_writeLock)
        {
            var current = _snapshot;
            var doomed = current.Entries.Values.Where(e => string.Equals(e.Chunk.SourceId, sourceId, StringComparison.Ordinal)).ToArray();
            if (doomed.Length == 0)
            {
                return Task.CompletedTask;
            }

            var entries = new Dictionary<string, Entry>(current.Entries, StringComparer.Ordinal);
            var df = new Dictionary<string, int>(current.DocumentFrequency, StringComparer.Ordinal);
            var totalLength = current.TotalLength;
            foreach (var entry in doomed)
            {
                entries.Remove(entry.Chunk.Id);
                Remove(entry, df, ref totalLength);
            }

            _snapshot = new Snapshot(entries, df, totalLength);
        }

        return Task.CompletedTask;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot.Entries.Count);

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = _snapshot;
        if (topK <= 0 || snapshot.Entries.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
        }

        var n = snapshot.Entries.Count;
        var avgLen = (double)snapshot.TotalLength / n;
        var queryTokens = TextNormalizer.Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();

        // Per-query IDF: computed once per distinct query token, not once per (token, entry) pair.
        var idf = new double[queryTokens.Length];
        for (var t = 0; t < queryTokens.Length; t++)
        {
            var docFreq = snapshot.DocumentFrequency.GetValueOrDefault(queryTokens[t]);
            idf[t] = Math.Log(1 + (n - docFreq + 0.5) / (docFreq + 0.5));
        }

        var queryNorm = TensorPrimitives.Norm(queryEmbedding.Span);
        var lexical = new List<(Entry Entry, double Score)>(n);
        var vector = new List<(Entry Entry, double Score)>(n);
        foreach (var entry in snapshot.Entries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bm25 = Bm25(entry, queryTokens, idf, avgLen);
            if (bm25 > 0)
            {
                lexical.Add((entry, bm25));
            }

            var cosine = Cosine(entry, queryEmbedding.Span, queryNorm);
            if (cosine > 0)
            {
                vector.Add((entry, cosine));
            }
        }

        // Rank each list independently, then fuse: score = sum over lists of 1 / (k + rank).
        var fused = new Dictionary<string, (Entry Entry, double Score)>(StringComparer.Ordinal);
        AddRanks(fused, lexical.OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Chunk.Id, StringComparer.Ordinal));
        AddRanks(fused, vector.OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Chunk.Id, StringComparer.Ordinal));

        var results = fused.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Chunk.Id, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => new RetrievedChunk(x.Entry.Chunk, Math.Round(x.Score, 6), "hybrid-rrf"))
            .ToArray();

        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(results);
    }

    private static void AddRanks(Dictionary<string, (Entry Entry, double Score)> fused, IEnumerable<(Entry Entry, double Score)> ranked)
    {
        var rank = 0;
        foreach (var (entry, _) in ranked)
        {
            rank++;
            var existing = fused.TryGetValue(entry.Chunk.Id, out var found) ? found.Score : 0;
            fused[entry.Chunk.Id] = (entry, existing + 1.0 / (RrfK + rank));
        }
    }

    private static double Bm25(Entry entry, string[] queryTokens, double[] idf, double avgLen)
    {
        if (entry.Length == 0 || avgLen == 0)
        {
            return 0;
        }

        double score = 0;
        for (var t = 0; t < queryTokens.Length; t++)
        {
            if (!entry.TermFrequency.TryGetValue(queryTokens[t], out var tf))
            {
                continue;
            }

            var norm = tf * (Bm25K1 + 1) / (tf + Bm25K1 * (1 - Bm25B + Bm25B * entry.Length / avgLen));
            score += idf[t] * norm;
        }

        return score;
    }

    private static double Cosine(Entry entry, ReadOnlySpan<float> query, float queryNorm)
    {
        var a = entry.Embedding.Span;
        if (a.Length == 0 || a.Length != query.Length || entry.Norm == 0 || queryNorm == 0)
        {
            return 0;
        }

        // Vectors are L2-normalised by the embedder, but we do not rely on it; the entry norm is stored once.
        return TensorPrimitives.Dot(a, query) / (entry.Norm * queryNorm);
    }

    private static void Add(Entry entry, Dictionary<string, int> df, ref long totalLength)
    {
        totalLength += entry.Length;
        foreach (var term in entry.TermFrequency.Keys)
        {
            df[term] = df.GetValueOrDefault(term) + 1;
        }
    }

    private static void Remove(Entry entry, Dictionary<string, int> df, ref long totalLength)
    {
        totalLength -= entry.Length;
        foreach (var term in entry.TermFrequency.Keys)
        {
            var remaining = df.GetValueOrDefault(term) - 1;
            if (remaining <= 0)
            {
                df.Remove(term);
            }
            else
            {
                df[term] = remaining;
            }
        }
    }

    private sealed record Entry(Chunk Chunk, ReadOnlyMemory<float> Embedding, float Norm, Dictionary<string, int> TermFrequency, int Length)
    {
        public static Entry Create(Chunk chunk, ReadOnlyMemory<float> embedding)
        {
            var tokens = TextNormalizer.Tokenize(chunk.Text);
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                tf[token] = tf.GetValueOrDefault(token) + 1;
            }

            return new Entry(chunk, embedding, TensorPrimitives.Norm(embedding.Span), tf, tokens.Count);
        }
    }

    private sealed record Snapshot(Dictionary<string, Entry> Entries, Dictionary<string, int> DocumentFrequency, long TotalLength)
    {
        public static readonly Snapshot Empty = new(new Dictionary<string, Entry>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal), 0);
    }
}
