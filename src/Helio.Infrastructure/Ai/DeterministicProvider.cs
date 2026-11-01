using System.Globalization;
using System.Text.Json;
using Helio.Application.Text;
using Helio.Domain;

namespace Helio.Infrastructure.Ai;

/// <summary>
/// Offline embedder: feature-hashed bag of words with sublinear term weighting, L2-normalised.
/// Identical text always yields an identical vector, which makes retrieval evaluation reproducible
/// and lets the full pipeline run in CI at zero model cost. Not a semantic model: it is the
/// deterministic stand-in that the Azure OpenAI embedder replaces in production.
/// </summary>
public sealed class HashingEmbedder(int dimensions = 512) : ITextEmbedder
{
    public string ModelVersion => $"hashing-{Dimensions}";

    public int Dimensions { get; } = dimensions;

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var result = new ReadOnlyMemory<float>[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[i] = Embed(texts[i]);
        }

        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(result);
    }

    private ReadOnlyMemory<float> Embed(string text)
    {
        var vector = new float[Dimensions];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in TextNormalizer.Tokenize(text))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        foreach (var (token, count) in counts)
        {
            var hash = StableHash(token);
            var index = (int)(hash % (uint)Dimensions);
            var sign = (hash & 0x80000000u) == 0 ? 1f : -1f; // a second hash bit decides the sign, as in the hashing trick
            vector[index] += sign * (1f + MathF.Log(count));
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    /// <summary>FNV-1a; stable across processes and platforms, unlike string.GetHashCode.</summary>
    internal static uint StableHash(string token)
    {
        var hash = 2166136261u;
        foreach (var c in token)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }
}

/// <summary>
/// Price table in USD per million tokens, bound from configuration. An unknown model version costs zero,
/// which would silently disable the spend alert, so it is reported once per version through
/// <see cref="UnknownModelVersions"/> and the warning callback.
/// </summary>
public sealed class ConfiguredCostModel(IReadOnlyDictionary<string, ModelPrice> prices, Action<string>? onUnknownModel = null) : Application.ICostModel
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unknown = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> UnknownModelVersions => _unknown.Keys.ToArray();

    public long EstimateMicroUsd(string modelVersion, int inputTokens, int outputTokens)
    {
        if (!prices.TryGetValue(modelVersion, out var price))
        {
            if (_unknown.TryAdd(modelVersion, 0))
            {
                onUnknownModel?.Invoke(modelVersion);
            }

            return 0;
        }

        var usd = (inputTokens * price.InputPerMillionUsd + outputTokens * price.OutputPerMillionUsd) / 1_000_000m;
        return (long)Math.Round(usd * 1_000_000m, MidpointRounding.AwayFromZero);
    }

    public static ConfiguredCostModel FromDictionary(IDictionary<string, string> raw, Action<string>? onUnknownModel = null)
    {
        var map = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, value) in raw)
        {
            var parts = value.Split('/', 2);
            if (parts.Length == 2
                && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var input)
                && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var output))
            {
                map[model] = new ModelPrice(input, output);
            }
        }

        return new ConfiguredCostModel(map, onUnknownModel);
    }
}

public sealed record ModelPrice(decimal InputPerMillionUsd, decimal OutputPerMillionUsd);
