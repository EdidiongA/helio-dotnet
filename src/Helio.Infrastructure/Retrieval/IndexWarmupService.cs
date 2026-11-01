using Helio.Domain;
using Helio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helio.Infrastructure.Retrieval;

/// <summary>
/// The in-memory store is derived state. On start-up it is rebuilt from the database (the system
/// of record) so a restart never loses the index. Stored vectors are reused when they came from the
/// configured embedding model, so a restart costs no model calls; only chunks embedded by a different
/// model (or none) are re-embedded. The whole corpus is published in one upsert, so statistics are
/// computed once rather than once per document. Runs before the host accepts traffic.
/// </summary>
public sealed partial class IndexWarmupService(IServiceProvider services, ILogger<IndexWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var embedder = scope.ServiceProvider.GetRequiredService<ITextEmbedder>();

        await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

        if (store is not InMemoryHybridStore)
        {
            return; // Azure AI Search persists its own index; nothing to rebuild.
        }

        // Two cheap queries instead of an Include inside a GroupBy, whose translation differs by provider.
        var latestIds = await db.Documents.LatestPerSource().Select(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (latestIds.Count == 0)
        {
            LogWarmed(logger, 0, 0, 0);
            return;
        }

        var rows = await db.Chunks.AsNoTracking()
            .Where(c => latestIds.Contains(c.DocumentId))
            .OrderBy(c => c.DocumentId).ThenBy(c => c.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var chunks = new List<Chunk>(rows.Count);
        var vectors = new List<ReadOnlyMemory<float>>(rows.Count);
        var pendingChunks = new List<Chunk>();
        foreach (var row in rows)
        {
            var chunk = row.ToDomain();
            if (row.Vector is { Length: > 0 } && string.Equals(row.EmbeddingModel, embedder.ModelVersion, StringComparison.Ordinal))
            {
                chunks.Add(chunk);
                vectors.Add(VectorCodec.Decode(row.Vector));
            }
            else
            {
                pendingChunks.Add(chunk);
            }
        }

        if (pendingChunks.Count > 0)
        {
            var fresh = await embedder.EmbedAsync(pendingChunks.Select(c => c.Text).ToArray(), cancellationToken).ConfigureAwait(false);
            chunks.AddRange(pendingChunks);
            vectors.AddRange(fresh);
        }

        await store.UpsertAsync(chunks, vectors, cancellationToken).ConfigureAwait(false);
        LogWarmed(logger, latestIds.Count, chunks.Count, pendingChunks.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Creates the schema when it is missing. Several replicas may start against a fresh database at once;
    /// the loser of that race sees a "table already exists" error, which is harmless once the schema is
    /// confirmed present. Production deployments should create the schema in the pipeline instead
    /// (see docs/DECISIONS.md, ADR-013); this keeps first-run developer experience intact.
    /// </summary>
    private async Task EnsureSchemaAsync(HelioDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!await SchemaPresentAsync(db, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            LogSchemaRace(logger, ex);
        }
    }

    private static async Task<bool> SchemaPresentAsync(HelioDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Documents.AsNoTracking().Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Index warmed from database: {Documents} documents, {Chunks} chunks ({ReEmbedded} re-embedded)")]
    private static partial void LogWarmed(ILogger logger, int documents, int chunks, int reEmbedded);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Schema creation raced with another replica; continuing because the schema is present")]
    private static partial void LogSchemaRace(ILogger logger, Exception exception);
}

/// <summary>Deletes query records older than the configured retention, once an hour. Disabled when retention is zero.</summary>
public sealed partial class QueryRecordRetentionService(IServiceProvider services, Microsoft.Extensions.Options.IOptions<RetentionOptions> options, ILogger<QueryRecordRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = options.Value.QueryRecordRetention;
        if (retention <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PurgeInterval);
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var records = scope.ServiceProvider.GetRequiredService<IQueryRecordRepository>();
                var purged = await records.PurgeOlderThanAsync(DateTimeOffset.UtcNow - retention, stoppingToken).ConfigureAwait(false);
                if (purged > 0)
                {
                    LogPurged(logger, purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPurgeFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Purged {Count} query records past retention")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "Query record purge failed; will retry next interval")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}

/// <summary>Data-governance settings for the audit log.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>How long question and answer records are kept. Zero keeps them forever (development default).</summary>
    public TimeSpan QueryRecordRetention { get; init; } = TimeSpan.Zero;

    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromHours(1);
}
