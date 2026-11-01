using Helio.Application.Answering;

namespace Helio.Application;

/// <summary>Answer cache port. Implemented in infrastructure with an in-memory or distributed cache.</summary>
public interface IAnswerCache
{
    Task<AskResponse?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, AskResponse value, TimeSpan ttl, CancellationToken cancellationToken);
}

/// <summary>
/// A knowledge-base generation marker shared by every replica. Ingestion bumps it after a successful
/// write; the answer cache folds it into its keys so no cached answer outlives the index it came from.
/// </summary>
public interface IIndexGeneration
{
    Task<string> CurrentAsync(CancellationToken cancellationToken);

    Task BumpAsync(CancellationToken cancellationToken);
}

/// <summary>Estimates spend per request from token counts. Prices live in configuration, not code.</summary>
public interface ICostModel
{
    long EstimateMicroUsd(string modelVersion, int inputTokens, int outputTokens);
}
