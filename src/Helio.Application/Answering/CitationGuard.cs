using Helio.Domain;

namespace Helio.Application.Answering;

/// <summary>
/// Enforces the one rule that removes the most embarrassing RAG failure: an answer that cites
/// a document which was never retrieved. Citations are constrained to the chunks retrieved for
/// this request; anything else is stripped, and an answer left without a valid citation is
/// downgraded to an abstention rather than served.
/// </summary>
public static class CitationGuard
{
    public static Answer Apply(ModelDraft draft, IReadOnlyList<RetrievedChunk> retrieved)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(retrieved);

        if (draft.Abstained)
        {
            // The model's own explanation is kept for operators but never served verbatim: an abstention
            // carries no citation requirement, so it is the one path where injected text could reach a user.
            return Answer.Abstain("model_abstained") with { Detail = Sanitize(draft.Answer) };
        }

        var byId = retrieved.ToDictionary(r => r.Chunk.Id, r => r.Chunk, StringComparer.Ordinal);
        var citations = new List<Citation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in draft.CitationChunkIds)
        {
            if (byId.TryGetValue(id, out var chunk) && seen.Add(id))
            {
                citations.Add(new Citation(chunk.Id, chunk.SourceId, chunk.Title, Excerpt(chunk.Text)));
            }
        }

        if (citations.Count == 0)
        {
            return Answer.Abstain(draft.CitationChunkIds.Count == 0 ? "no_citations" : "citations_not_in_context");
        }

        return new Answer
        {
            Text = draft.Answer.Trim(),
            Citations = citations,
            Confidence = Math.Clamp(draft.Confidence, 0, 1),
            Abstained = false,
        };
    }

    private static string Excerpt(string text, int maxChars = 240) => Text.TextNormalizer.TruncateGraphemes(text.ReplaceLineEndings(" "), maxChars);

    private static string? Sanitize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Text.TextNormalizer.TruncateGraphemes(text.ReplaceLineEndings(" ").Trim(), 300);
}
