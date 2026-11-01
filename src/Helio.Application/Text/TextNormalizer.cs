using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Helio.Application.Text;

/// <summary>Shared, allocation-conscious text helpers used by chunking, retrieval and evaluation.</summary>
public static partial class TextNormalizer
{
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\r\n?", RegexOptions.CultureInvariant)]
    private static partial Regex LineEndingRegex();

    [GeneratedRegex(@"(?<=[.!?。！？])\s+(?=[\p{Lu}\p{Nd}\(])|\n+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "if", "in", "is", "it", "its",
        "my", "of", "on", "or", "should", "that", "the", "this", "to", "was", "what", "when", "which", "why", "will", "with", "you", "your",
    };

    /// <summary>Lowercases and splits into alphanumeric tokens, dropping stop words. Used for lexical matching only.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = NonWordRegex().Split(text.ToLowerInvariant());
        var tokens = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length > 1 && !StopWords.Contains(part))
            {
                tokens.Add(Stem(part));
            }
        }

        return tokens;
    }

    /// <summary>
    /// Very light suffix stripping so "runs", "washed" and "charging" match "run", "wash" and "charge".
    /// Deliberately conservative: it never touches short tokens or numbers, and it is applied
    /// identically at index and query time, which is all a lexical matcher needs.
    /// </summary>
    internal static string Stem(string token)
    {
        if (token.Length <= 3 || char.IsDigit(token[0]))
        {
            return token;
        }

        if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5)
        {
            return token[..^3];
        }

        if (token.EndsWith("ied", StringComparison.Ordinal))
        {
            return string.Concat(token.AsSpan(0, token.Length - 3), "y");
        }

        if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4)
        {
            return token[..^2];
        }

        if (token.EndsWith("ies", StringComparison.Ordinal))
        {
            return string.Concat(token.AsSpan(0, token.Length - 3), "y");
        }

        if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4 && token[^3] is 's' or 'x' or 'h')
        {
            return token[..^2];
        }

        if (token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal))
        {
            token = token[..^1];
        }

        // Drop a trailing silent e so "charge" and "charging" (stemmed to "charg") agree.
        return token.Length > 4 && token.EndsWith('e') ? token[..^1] : token;
    }

    public static string NormalizeLineEndings(string text) => LineEndingRegex().Replace(text, "\n");

    /// <summary>
    /// Canonical Unicode composition (NFC). The same word typed with a composed or a decomposed accent
    /// must hash, tokenise and embed identically, otherwise a re-ingest from a different editor creates a
    /// spurious new version and a question misses the chunk it quotes.
    /// </summary>
    public static string NormalizeUnicode(string text) => text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);

    /// <summary>Truncates at a text-element boundary so a surrogate pair or combining sequence is never split.</summary>
    public static string TruncateGraphemes(string text, int maxChars, string suffix = "…")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars)
        {
            return text;
        }

        var limit = Math.Max(0, maxChars - suffix.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var end = 0;
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + enumerator.GetTextElement().Length;
            if (next > limit)
            {
                break;
            }

            end = next;
        }

        return string.Concat(text.AsSpan(0, end), suffix);
    }

    public static IReadOnlyList<string> SplitSentences(string paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        // Collapse runs of blanks first so the boundary regex never backtracks over a long whitespace run.
        var collapsed = WhitespaceRunRegex().Replace(paragraph, " ");
        return SentenceBoundaryRegex().Split(collapsed).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    }

    [GeneratedRegex(@"[ \t\f\v]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRunRegex();

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Jaccard-style overlap of query tokens covered by a candidate: |Q ∩ C| / |Q|.</summary>
    public static double QueryCoverage(IReadOnlyList<string> queryTokens, IReadOnlyList<string> candidateTokens)
    {
        if (queryTokens.Count == 0)
        {
            return 0;
        }

        var candidate = new HashSet<string>(candidateTokens, StringComparer.Ordinal);
        var distinctQuery = new HashSet<string>(queryTokens, StringComparer.Ordinal);
        var hits = distinctQuery.Count(candidate.Contains);
        return (double)hits / distinctQuery.Count;
    }
}
