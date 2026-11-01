using Helio.Domain;

namespace Helio.Application.Text;

/// <summary>
/// Splits on blank lines so each chunk is a self-contained paragraph (a claim cluster),
/// then packs short paragraphs together and splits over-long ones on sentence boundaries.
/// Paragraph chunking beats fixed token windows on reference material because numeric
/// claims ("55.2 to 56.0 V") stay attached to their qualifiers ("for a 48 V bank").
/// </summary>
public sealed class ParagraphChunker(ChunkerOptions? options = null) : IChunker
{
    private readonly ChunkerOptions _options = options ?? new ChunkerOptions();

    public IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = TextNormalizer.NormalizeLineEndings(TextNormalizer.NormalizeUnicode(text));
        // Markdown headings are structure, not content: the document title is stored separately and a
        // heading line inside a chunk would otherwise be scored as a sentence.
        var paragraphs = normalized
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => string.Join(' ', p.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => !line.StartsWith('#'))))
            .Where(p => p.Length > 0)
            .ToList();

        var chunks = new List<string>();
        var buffer = new List<string>();
        var bufferWords = 0;

        foreach (var paragraph in paragraphs)
        {
            var words = WordCount(paragraph);
            if (words > _options.MaxWords)
            {
                Flush();
                chunks.AddRange(SplitLongParagraph(paragraph));
                continue;
            }

            if (bufferWords + words > _options.MaxWords && buffer.Count > 0)
            {
                Flush();
            }

            buffer.Add(paragraph);
            bufferWords += words;

            if (bufferWords >= _options.MinWords)
            {
                Flush();
            }
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            chunks.Add(string.Join("\n\n", buffer));
            buffer.Clear();
            bufferWords = 0;
        }
    }

    private IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        var sentences = TextNormalizer.SplitSentences(paragraph);
        var current = new List<string>();
        var currentWords = 0;
        foreach (var sentence in sentences.SelectMany(HardSplit))
        {
            var words = WordCount(sentence);
            if (currentWords + words > _options.MaxWords && current.Count > 0)
            {
                yield return string.Join(' ', current);
                current.Clear();
                currentWords = 0;
            }

            current.Add(sentence);
            currentWords += words;
        }

        if (current.Count > 0)
        {
            yield return string.Join(' ', current);
        }
    }

    /// <summary>
    /// A single "sentence" longer than the chunk limit (a log dump, a table flattened to one line, prose in a
    /// script with no terminators the splitter recognises) is cut into fixed word windows. Without this one
    /// paragraph could exceed the embedding model's input limit and fail every question that retrieves it.
    /// </summary>
    private IEnumerable<string> HardSplit(string sentence)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= _options.MaxWords)
        {
            yield return sentence;
            yield break;
        }

        for (var offset = 0; offset < words.Length; offset += _options.MaxWords)
        {
            yield return string.Join(' ', words.Skip(offset).Take(_options.MaxWords));
        }
    }

    private static int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}

public sealed class ChunkerOptions
{
    /// <summary>Flush once a packed chunk reaches this many words. Keeps chunks dense enough to carry a full claim.</summary>
    public int MinWords { get; init; } = 60;

    /// <summary>Never emit a chunk longer than this; over-long paragraphs are split on sentences.</summary>
    public int MaxWords { get; init; } = 220;
}
