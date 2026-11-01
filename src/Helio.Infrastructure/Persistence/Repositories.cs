using Helio.Domain;
using Microsoft.EntityFrameworkCore;

namespace Helio.Infrastructure.Persistence;

public sealed class EfDocumentRepository(HelioDbContext db) : IDocumentRepository
{
    public async Task<Document?> FindLatestAsync(string sourceId, CancellationToken cancellationToken)
    {
        // Header plus chunk count only; the full chunk set is never needed to decide whether to re-ingest.
        var row = await db.Documents
            .AsNoTracking()
            .Where(d => d.SourceId == sourceId)
            .OrderByDescending(d => d.Version)
            .Select(d => new { Row = d, ChunkCount = d.Chunks.Count })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row?.Row.ToDomainHeader(row.ChunkCount);
    }

    public async Task AddAsync(Document document, EmbeddingSet? embeddings, CancellationToken cancellationToken)
    {
        db.Documents.Add(DocumentRow.From(document, embeddings));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Documents.LatestPerSource()
            .Select(d => new { Row = d, ChunkCount = d.Chunks.Count })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.OrderBy(r => r.Row.Title, StringComparer.Ordinal).Select(r => r.Row.ToDomainHeader(r.ChunkCount)).ToArray();
    }
}

public sealed class EfQueryRecordRepository(HelioDbContext db) : IQueryRecordRepository
{
    public async Task AddAsync(QueryRecord record, CancellationToken cancellationToken)
    {
        db.QueryRecords.Add(QueryRecordRow.From(record));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QueryRecord>> RecentAsync(int take, CancellationToken cancellationToken)
    {
        var rows = await db.QueryRecords
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToArray();
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var cutoffUtc = cutoff.UtcDateTime;
        return db.QueryRecords.Where(r => r.CreatedAtUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
    }
}

internal static class DocumentQueries
{
    /// <summary>The single definition of "the current version of every source", shared by listing and warm-up.</summary>
    public static IQueryable<DocumentRow> LatestPerSource(this IQueryable<DocumentRow> documents) =>
        documents.AsNoTracking().Where(d => d.Version == documents.Where(o => o.SourceId == d.SourceId).Max(o => o.Version));
}
