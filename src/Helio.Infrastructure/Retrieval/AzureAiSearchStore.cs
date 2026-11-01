using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Helio.Domain;

namespace Helio.Infrastructure.Retrieval;

/// <summary>
/// Vector store over Azure AI Search with hybrid (keyword plus vector) queries, which the service
/// fuses with reciprocal rank fusion server-side. The index is created on first use if missing.
/// Security trimming by group is a one-line filter away: add a <c>groups</c> collection field and
/// pass <c>search.in(groups, ...)</c> in <see cref="SearchOptions.Filter"/>.
/// </summary>
public sealed class AzureAiSearchStore(SearchIndexClient indexClient, string indexName, int dimensions) : IVectorStore
{
    private const string VectorProfile = "helio-vector-profile";
    private const string VectorAlgorithm = "helio-hnsw";

    /// <summary>The service rejects batches over 1,000 actions or 16 MB; 500 keeps large chunks well inside both.</summary>
    private const int UploadBatchSize = 500;
    private const int DeletePageSize = 1000;
    private static readonly string[] ResultFields = ["chunkId", "documentId", "sourceId", "title", "ordinal", "text"];

    private readonly SearchClient _search = indexClient.GetSearchClient(indexName);
    private volatile bool _indexEnsured;

    public async Task UpsertAsync(IReadOnlyList<Chunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddings);
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < chunks.Count; offset += UploadBatchSize)
        {
            var slice = Enumerable.Range(offset, Math.Min(UploadBatchSize, chunks.Count - offset));
            var batch = IndexDocumentsBatch.Create(slice.Select(i => IndexDocumentsAction.MergeOrUpload(new ChunkIndexDocument
            {
                Id = ToKey(chunks[i].Id),
                ChunkId = chunks[i].Id,
                DocumentId = chunks[i].DocumentId.ToString(),
                SourceId = chunks[i].SourceId,
                Title = chunks[i].Title,
                Ordinal = chunks[i].Ordinal,
                Text = chunks[i].Text,
                Vector = embeddings[i].ToArray(),
            })).ToArray());

            var result = await _search.IndexDocumentsAsync(batch, new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken).ConfigureAwait(false);
            var failed = result.Value.Results.Where(r => !r.Succeeded).ToArray();
            if (failed.Length > 0)
            {
                throw new InvalidOperationException($"Azure AI Search rejected {failed.Length} of {batch.Actions.Count} chunk uploads; first error: {failed[0].ErrorMessage}");
            }
        }
    }

    public async Task DeleteBySourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        // Page until the filter returns nothing: a source can hold more than one page of chunks, and leaving
        // the surplus behind would keep superseded versions citeable.
        var filter = SearchFilter.Create($"sourceId eq {sourceId}", System.Globalization.CultureInfo.InvariantCulture);
        while (true)
        {
            var options = new SearchOptions { Filter = filter, Select = { "id" }, Size = DeletePageSize };
            var found = await _search.SearchAsync<ChunkIndexDocument>("*", options, cancellationToken).ConfigureAwait(false);
            var keys = new List<string>();
            await foreach (var result in found.Value.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                keys.Add(result.Document.Id);
            }

            if (keys.Count == 0)
            {
                return;
            }

            await _search.DeleteDocumentsAsync("id", keys, new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken).ConfigureAwait(false);
            if (keys.Count < DeletePageSize)
            {
                return;
            }
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        var count = await _search.GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);
        return (int)count.Value;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        var options = new SearchOptions
        {
            Size = topK,
            QueryType = SearchQueryType.Simple,
            SearchMode = SearchMode.Any,
            VectorSearch = new VectorSearchOptions
            {
                // A wider candidate pool than topK gives the service's RRF something to reorder.
                Queries = { new VectorizedQuery(queryEmbedding) { KNearestNeighborsCount = Math.Max(topK, 50), Fields = { "vector" } } },
            },
        };
        foreach (var field in ResultFields)
        {
            options.Select.Add(field); // never pull the 1,536-float vector back per hit
        }

        var response = await _search.SearchAsync<ChunkIndexDocument>(EscapeSimpleQuery(query), options, cancellationToken).ConfigureAwait(false);
        var results = new List<RetrievedChunk>(topK);
        await foreach (var hit in response.Value.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var d = hit.Document;
            results.Add(new RetrievedChunk(
                new Chunk { Id = d.ChunkId, DocumentId = Guid.Parse(d.DocumentId), SourceId = d.SourceId, Title = d.Title, Ordinal = d.Ordinal, Text = d.Text },
                hit.Score ?? 0,
                "azure-ai-search-hybrid"));
        }

        return results;
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexEnsured)
        {
            return;
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
                new SimpleField("chunkId", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("documentId", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("sourceId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("title"),
                new SimpleField("ordinal", SearchFieldDataType.Int32),
                new SearchableField("text"),
                new VectorSearchField("vector", dimensions, VectorProfile),
            },
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(VectorProfile, VectorAlgorithm) },
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgorithm) },
            },
        };

        await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken).ConfigureAwait(false);
        _indexEnsured = true;
    }

    /// <summary>Simple query syntax treats + | - * " ( ) \ as operators; an unbalanced quote in a question would otherwise be a 400 from the service.</summary>
    internal static string EscapeSimpleQuery(string query) => System.Text.RegularExpressions.Regex.Replace(query, @"[+\-|*""()\\]", m => "\\" + m.Value);

    /// <summary>Index keys may only contain letters, digits, underscore, dash and equals; chunk ids contain '::'.</summary>
    private static string ToKey(string chunkId) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(chunkId)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class ChunkIndexDocument
    {
        public string Id { get; set; } = string.Empty;

        public string ChunkId { get; set; } = string.Empty;

        public string DocumentId { get; set; } = string.Empty;

        public string SourceId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int Ordinal { get; set; }

        public string Text { get; set; } = string.Empty;

        public float[] Vector { get; set; } = [];
    }
}
