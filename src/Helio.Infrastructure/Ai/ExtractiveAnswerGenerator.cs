using System.Text.Json;
using Helio.Application.Answering;
using Helio.Application.Text;
using Helio.Domain;

namespace Helio.Infrastructure.Ai;

/// <summary>
/// Offline generator: an extractive answerer that behaves like a cautious model.
/// <para>
/// Every sentence in the retrieved context is scored by how much of the question it covers, with each
/// question token weighted by its rarity across the context (a decisive word such as "fuse" outweighs a
/// pervasive one such as "battery"). The best sentence plus its best neighbour form the answer, each
/// citing the chunk it came from. When weighted coverage falls below the threshold the generator
/// abstains. That guard exists because of a real evaluation failure: without it an unanswerable canary
/// question received a confident, wrong extraction.
/// </para>
/// <para>
/// The reply is emitted in the same JSON contract a live model is asked for, so the parser and the
/// citation guard are exercised offline, in CI, at zero model cost.
/// </para>
/// </summary>
public sealed class ExtractiveAnswerGenerator(double coverageThreshold = 0.45) : IAnswerGenerator
{
    public string ProviderName => "deterministic";

    public string ModelVersion => "extractive-v3";

    /// <summary>The extractive generator always emits valid JSON, so a repair is simply a second generation.</summary>
    public Task<GenerationResult> RepairAsync(string question, IReadOnlyList<RetrievedChunk> context, string previousRawText, CancellationToken cancellationToken) =>
        GenerateAsync(question, context, cancellationToken);

    public Task<GenerationResult> GenerateAsync(string question, IReadOnlyList<RetrievedChunk> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var queryTokens = TextNormalizer.Tokenize(question).Distinct(StringComparer.Ordinal).ToArray();
        var sentences = Flatten(context);
        var weights = RarityWeights(queryTokens, sentences);
        var totalWeight = weights.Values.Sum();

        var scored = new double[sentences.Count];
        for (var i = 0; i < sentences.Count; i++)
        {
            scored[i] = totalWeight == 0 ? 0 : weights.Where(w => sentences[i].Tokens.Contains(w.Key)).Sum(w => w.Value) / totalWeight;
        }

        var bestIndex = IndexOfMax(scored);
        var inputTokens = context.Sum(c => c.Chunk.ApproximateTokens) + queryTokens.Length + 120;

        // Grow a window of up to three adjacent sentences from the same chunk while neighbours add coverage,
        // then decide on the window: reference text often states the subject in one sentence and the figure in the next.
        var window = bestIndex < 0 ? [] : GrowWindow(sentences, bestIndex, weights);
        var covered = new HashSet<string>(window.SelectMany(w => w.Tokens), StringComparer.Ordinal);
        var windowCoverage = totalWeight == 0 ? 0 : weights.Where(w => covered.Contains(w.Key)).Sum(w => w.Value) / totalWeight;

        string json;
        if (window.Count == 0 || windowCoverage < coverageThreshold)
        {
            json = JsonSerializer.Serialize(new
            {
                answer = "The knowledge base does not contain enough evidence to answer this question reliably.",
                citations = Array.Empty<string>(),
                confidence = Math.Round(windowCoverage, 2),
                abstained = true,
            });
        }
        else
        {
            json = JsonSerializer.Serialize(new
            {
                answer = string.Join(' ', window.Select(p => p.Text)),
                citations = window.Select(p => p.ChunkId).Distinct(StringComparer.Ordinal).ToArray(),
                confidence = Math.Round(Math.Min(0.95, 0.5 + windowCoverage / 2), 2),
                abstained = false,
            });
        }

        var draft = DraftParser.TryParse(json, out var error);
        var outputTokens = (int)Math.Ceiling(json.Length / 4.0);
        return Task.FromResult(new GenerationResult(json, draft, inputTokens, outputTokens, error));
    }

    private static List<Sentence> Flatten(IReadOnlyList<RetrievedChunk> context)
    {
        var list = new List<Sentence>();
        var order = 0;
        foreach (var retrieved in context)
        {
            foreach (var text in TextNormalizer.SplitSentences(retrieved.Chunk.Text))
            {
                var tokens = new HashSet<string>(TextNormalizer.Tokenize(text), StringComparer.Ordinal);
                if (tokens.Count > 0)
                {
                    list.Add(new Sentence(order++, retrieved.Chunk.Id, text, tokens));
                }
            }
        }

        return list;
    }

    /// <summary>Weight = log(1 + N / (df + 1)), where df is the number of context sentences containing the token. Absent tokens weigh the most, which keeps unanswerable questions below threshold.</summary>
    private static Dictionary<string, double> RarityWeights(string[] queryTokens, List<Sentence> sentences)
    {
        var n = Math.Max(1, sentences.Count);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var token in queryTokens)
        {
            var df = sentences.Count(s => s.Tokens.Contains(token));
            weights[token] = Math.Log(1 + (double)n / (df + 1));
        }

        return weights;
    }

    private static int IndexOfMax(double[] values)
    {
        var best = -1;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] > 0 && (best < 0 || values[i] > values[best]))
            {
                best = i;
            }
        }

        return best;
    }

    private static List<Sentence> GrowWindow(List<Sentence> sentences, int bestIndex, Dictionary<string, double> weights, int maxSentences = 3)
    {
        var best = sentences[bestIndex];
        var covered = new HashSet<string>(best.Tokens, StringComparer.Ordinal);
        int lo = bestIndex, hi = bestIndex;

        while (hi - lo + 1 < maxSentences)
        {
            var left = Candidate(lo - 1);
            var right = Candidate(hi + 1);
            var leftGain = left is null ? 0 : Gain(left);
            var rightGain = right is null ? 0 : Gain(right);
            if (leftGain <= 0 && rightGain <= 0)
            {
                break;
            }

            if (rightGain >= leftGain)
            {
                hi++;
                covered.UnionWith(sentences[hi].Tokens);
            }
            else
            {
                lo--;
                covered.UnionWith(sentences[lo].Tokens);
            }
        }

        return sentences.GetRange(lo, hi - lo + 1);

        Sentence? Candidate(int j) => j >= 0 && j < sentences.Count && sentences[j].ChunkId == best.ChunkId ? sentences[j] : null;
        double Gain(Sentence s) => weights.Where(w => s.Tokens.Contains(w.Key) && !covered.Contains(w.Key)).Sum(w => w.Value);
    }

    private sealed record Sentence(int Order, string ChunkId, string Text, HashSet<string> Tokens);
}
