using Helio.Application.Answering;
using Helio.Application.Text;
using Helio.Domain;

namespace Helio.Application.Evaluation;

/// <summary>Scores an answer against its context (faithfulness) and question (relevance). Deterministic or LLM-judged.</summary>
public interface IAnswerJudge
{
    string Name { get; }

    Task<(double Faithfulness, double AnswerRelevance)> JudgeAsync(string question, string answer, IReadOnlyList<RetrievedChunk> context, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the golden set through the live pipeline and classifies every case into the six-way taxonomy.
/// Retrieval is judged first (was the expected source in the retrieved set?) so a wrong answer is
/// attributed to the component that caused it.
/// </summary>
public sealed class EvaluationRunner(AskQuestionHandler ask, IAnswerJudge judge)
{
    public async Task<EvalSummary> RunAsync(IReadOnlyList<GoldenCase> cases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var results = new List<EvalCaseResult>(cases.Count);

        foreach (var golden in cases)
        {
            var response = await ask.HandleAsync(new AskRequest(golden.Question, CallerHash: "eval", BypassCache: true), cancellationToken).ConfigureAwait(false);
            var answer = response.Answer;
            var retrievedSources = response.Retrieved.Select(r => r.Chunk.SourceId).ToHashSet(StringComparer.Ordinal);
            var retrievalHit = golden.ExpectedSourceId is null || retrievedSources.Contains(golden.ExpectedSourceId);
            var (faithfulness, relevance) = answer.Abstained
                ? (1.0, 0.0)
                : await judge.JudgeAsync(golden.Question, answer.Text, response.Retrieved, cancellationToken).ConfigureAwait(false);

            var outcome = Classify(golden, answer, retrievalHit, response.Retrieved);
            results.Add(new EvalCaseResult(
                golden.Id,
                golden.Category,
                outcome,
                retrievalHit,
                faithfulness,
                relevance,
                response.TotalMs,
                answer.Text,
                answer.Citations.Select(c => c.ChunkId).ToArray()));
        }

        return Summarize(results);
    }

    internal static EvalOutcome Classify(GoldenCase golden, Answer answer, bool retrievalHit, IReadOnlyList<RetrievedChunk> retrieved)
    {
        // Forbidden phrases are checked on whatever text would be served, abstention or not: an injected
        // passage that talks the model into "abstaining" with a planted figure is still a false answer.
        if (ContainsAny(answer.Text, golden.ForbiddenPhrases))
        {
            return EvalOutcome.FalseAnswer;
        }

        if (golden.ShouldAbstain)
        {
            return answer.Abstained ? EvalOutcome.CorrectAbstention : EvalOutcome.FalseAnswer;
        }

        if (!retrievalHit)
        {
            return EvalOutcome.RetrievalFailure;
        }

        if (answer.Abstained)
        {
            return golden.AllowAbstain ? EvalOutcome.CorrectAbstention : EvalOutcome.GenerationFailure;
        }

        var retrievedIds = retrieved.Select(r => r.Chunk.Id).ToHashSet(StringComparer.Ordinal);
        if (answer.Citations.Any(c => !retrievedIds.Contains(c.ChunkId)))
        {
            return EvalOutcome.CitationMismatch;
        }

        return ContainsKeyphrases(answer.Text, golden.ExpectedKeyphrases) ? EvalOutcome.Correct : EvalOutcome.GenerationFailure;
    }

    /// <summary>At least one expected keyphrase must appear in the answer, compared case-insensitively with whitespace collapsed.</summary>
    internal static bool ContainsKeyphrases(string answer, IReadOnlyList<string> keyphrases) => keyphrases.Count == 0 || ContainsAny(answer, keyphrases);

    internal static bool ContainsAny(string text, IReadOnlyList<string> phrases)
    {
        if (phrases.Count == 0)
        {
            return false;
        }

        var haystack = Collapse(text);
        return phrases.Any(p => p.Length > 0 && haystack.Contains(Collapse(p), StringComparison.OrdinalIgnoreCase));
    }

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static EvalSummary Summarize(List<EvalCaseResult> results)
    {
        var outcomes = Enum.GetValues<EvalOutcome>().ToDictionary(o => o, o => results.Count(r => r.Outcome == o));
        var correct = outcomes[EvalOutcome.Correct] + outcomes[EvalOutcome.CorrectAbstention];
        var latencies = results.Select(r => r.LatencyMs).OrderBy(x => x).ToArray();
        var answered = results.Where(r => r.Outcome is not (EvalOutcome.CorrectAbstention or EvalOutcome.GenerationFailure) || r.AnswerRelevance > 0).ToArray();

        return new EvalSummary(
            results.Count,
            results.Count == 0 ? 0 : (double)correct / results.Count,
            results.Count == 0 ? 0 : results.Count(r => r.RetrievalHit) / (double)results.Count,
            answered.Length == 0 ? 0 : answered.Average(r => r.Faithfulness),
            answered.Length == 0 ? 0 : answered.Average(r => r.AnswerRelevance),
            outcomes[EvalOutcome.FalseAnswer],
            results.Count == 0 ? 0 : results.Count(r => r.Outcome is EvalOutcome.CorrectAbstention || (r.Outcome == EvalOutcome.GenerationFailure && r.CitedChunkIds.Count == 0)) / (double)results.Count,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            outcomes,
            results);
    }

    private static int Percentile(int[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Deterministic judge: faithfulness is the share of answer sentences whose content tokens are covered
/// by the context; relevance is the share of question tokens covered by the answer. Cheap, reproducible,
/// and good enough to catch regressions in CI. Swap in an LLM judge for nuance.
/// </summary>
public sealed class LexicalAnswerJudge : IAnswerJudge
{
    public string Name => "lexical";

    public Task<(double Faithfulness, double AnswerRelevance)> JudgeAsync(string question, string answer, IReadOnlyList<RetrievedChunk> context, CancellationToken cancellationToken)
    {
        var contextTokens = new HashSet<string>(context.SelectMany(c => TextNormalizer.Tokenize(c.Chunk.Text)), StringComparer.Ordinal);
        var sentences = TextNormalizer.SplitSentences(answer);
        var supported = 0;
        var counted = 0;
        foreach (var sentence in sentences)
        {
            var tokens = TextNormalizer.Tokenize(sentence);
            if (tokens.Count == 0)
            {
                continue;
            }

            counted++;
            var covered = tokens.Count(contextTokens.Contains) / (double)tokens.Count;
            if (covered >= 0.6)
            {
                supported++;
            }
        }

        var faithfulness = counted == 0 ? 0 : (double)supported / counted;
        var relevance = TextNormalizer.QueryCoverage(TextNormalizer.Tokenize(question), TextNormalizer.Tokenize(answer));
        return Task.FromResult((faithfulness, relevance));
    }
}
