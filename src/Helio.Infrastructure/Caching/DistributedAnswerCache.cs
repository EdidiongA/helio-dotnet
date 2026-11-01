using System.Text.Json;
using Helio.Application;
using Helio.Application.Answering;
using Microsoft.Extensions.Caching.Distributed;

namespace Helio.Infrastructure.Caching;

/// <summary>
/// Answer cache over <see cref="IDistributedCache"/>, so the same code runs on the in-memory cache in
/// development and on Redis in production. Only non-abstained answers are cached, by the handler.
/// </summary>
public sealed class DistributedAnswerCache(IDistributedCache cache) : IAnswerCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AskResponse?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var bytes = await cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AskResponse>(bytes, Json);
        }
        catch (JsonException)
        {
            // A cache entry written by an older build is simply a miss.
            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    public Task SetAsync(string key, AskResponse value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, cancellationToken);
    }
}

/// <summary>No-op cache for tests and for deployments that disable caching.</summary>
public sealed class NullAnswerCache : IAnswerCache
{
    public Task<AskResponse?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<AskResponse?>(null);

    public Task SetAsync(string key, AskResponse value, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;
}
