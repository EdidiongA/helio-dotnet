using System.Text;
using Helio.Application;
using Microsoft.Extensions.Caching.Distributed;

namespace Helio.Infrastructure.Caching;

/// <summary>
/// Index generation marker stored in the same distributed cache as the answers, so every replica
/// behind one Redis sees a re-ingest at once. A missing key (first start, eviction) is replaced with
/// a fresh value rather than a constant, so nothing cached under an older marker can ever match again.
/// </summary>
public sealed class DistributedIndexGeneration(IDistributedCache cache) : IIndexGeneration
{
    private const string Key = "helio:index-generation";

    public async Task<string> CurrentAsync(CancellationToken cancellationToken)
    {
        var bytes = await cache.GetAsync(Key, cancellationToken).ConfigureAwait(false);
        if (bytes is { Length: > 0 })
        {
            return Encoding.UTF8.GetString(bytes);
        }

        var fresh = NewMarker();
        await cache.SetAsync(Key, Encoding.UTF8.GetBytes(fresh), new DistributedCacheEntryOptions(), cancellationToken).ConfigureAwait(false);
        return fresh;
    }

    public Task BumpAsync(CancellationToken cancellationToken) =>
        cache.SetAsync(Key, Encoding.UTF8.GetBytes(NewMarker()), new DistributedCacheEntryOptions(), cancellationToken);

    private static string NewMarker() => Guid.CreateVersion7().ToString("N");
}
