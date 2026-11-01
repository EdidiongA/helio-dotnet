using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Helio.Application.Text;

namespace Helio.UnitTests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Charging the battery", new[] { "charg", "battery" })]
    [InlineData("Use 35 mm2 copper for runs up to 2 m", new[] { "use", "35", "mm2", "copper", "run", "up" })]
    [InlineData("The, a, and of", new string[0])]
    public void Tokenize_LowercasesStemsAndDropsStopWords(string input, string[] expected)
    {
        Assert.Equal(expected, TextNormalizer.Tokenize(input));
    }

    [Theory]
    [InlineData("charge", "charg")]
    [InlineData("charging", "charg")]
    [InlineData("washed", "wash")]
    [InlineData("panels", "panel")]
    [InlineData("class", "class")]
    [InlineData("16s", "16s")]
    [InlineData("bus", "bus")]
    public void Stem_IsConservativeAndConsistent(string token, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Stem(token));
    }

    [Fact]
    public void SplitSentences_SplitsOnTerminatorsAndNewlines()
    {
        var sentences = TextNormalizer.SplitSentences("First one. Second one!\nThird (with 3.2 V) here. 4.5 kWh is fine.");
        Assert.Equal(["First one.", "Second one!", "Third (with 3.2 V) here.", "4.5 kWh is fine."], sentences);
    }

    [Fact]
    public void Sha256Hex_IsStable()
    {
        Assert.Equal(TextNormalizer.Sha256Hex("helio"), TextNormalizer.Sha256Hex("helio"));
        Assert.Equal(64, TextNormalizer.Sha256Hex("helio").Length);
    }

    [Property(MaxTest = 200)]
    public Property Tokenize_NeverThrowsAndNeverReturnsEmptyTokens(NonNull<string> input)
    {
        var tokens = TextNormalizer.Tokenize(input.Get);
        return tokens.All(t => t.Length > 0).ToProperty();
    }

    [Property(MaxTest = 200)]
    public Property QueryCoverage_IsBetweenZeroAndOne(NonNull<string> query, NonNull<string> candidate)
    {
        var coverage = TextNormalizer.QueryCoverage(TextNormalizer.Tokenize(query.Get), TextNormalizer.Tokenize(candidate.Get));
        return (coverage is >= 0 and <= 1).ToProperty();
    }
}

public class ParagraphChunkerTests
{
    private readonly ParagraphChunker _chunker = new(new ChunkerOptions { MinWords = 10, MaxWords = 30 });

    [Fact]
    public void Split_PacksShortParagraphsAndDropsHeadings()
    {
        var text = "# Title\n\nShort para one.\n\nShort para two is here.\n\nA third short paragraph with more words to pass the minimum threshold now.";
        var chunks = _chunker.Split(text);
        Assert.All(chunks, c => Assert.DoesNotContain("# Title", c, StringComparison.Ordinal));
        Assert.True(chunks.Count >= 1);
        Assert.Contains(chunks, c => c.Contains("Short para one.", StringComparison.Ordinal));
    }

    [Fact]
    public void Split_SplitsOverLongParagraphsOnSentences()
    {
        var sentence = "This sentence has exactly eight words in it. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 10)).Trim();
        var chunks = _chunker.Split(text);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 30));
    }

    [Fact]
    public void Split_EmptyInputGivesNoChunks()
    {
        Assert.Empty(_chunker.Split("   \n\n  "));
    }

    [Fact]
    public void Split_IsDeterministic()
    {
        var text = "Alpha beta gamma delta epsilon zeta eta theta iota kappa.\n\nLambda mu nu xi omicron pi rho sigma tau upsilon phi chi.";
        Assert.Equal(_chunker.Split(text), _chunker.Split(text));
    }

    [Property(MaxTest = 100)]
    public Property Split_NeverLosesWords(NonEmptyString input)
    {
        var text = input.Get;
        var chunks = _chunker.Split(text);
        var expected = TextNormalizer.NormalizeLineEndings(text)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Count();
        var actual = chunks.Sum(c => c.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length);
        return (actual == expected).Label($"expected {expected} words, chunks held {actual}");
    }
}

public class ChunkerHardeningTests
{
    [Fact]
    public void Split_NeverEmitsAChunkLongerThanMaxWordsEvenWithoutSentenceTerminators()
    {
        var chunker = new ParagraphChunker(new ChunkerOptions { MinWords = 10, MaxWords = 40 });
        var oneGiantSentence = string.Join(' ', Enumerable.Range(0, 1_000).Select(i => $"token{i}"));
        var chunks = chunker.Split(oneGiantSentence);
        Assert.Equal(25, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 40));
        Assert.Equal(1_000, chunks.Sum(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)); // nothing lost
    }

    [Fact]
    public void Split_IsStableAcrossUnicodeNormalisationForms()
    {
        var composed = "Caf\u00e9 r\u00e9sum\u00e9 charging voltage.";
        var decomposed = "Cafe\u0301 re\u0301sume\u0301 charging voltage.";
        var chunker = new ParagraphChunker();
        Assert.Equal(chunker.Split(composed), chunker.Split(decomposed));
        Assert.Equal(TextNormalizer.Sha256Hex(TextNormalizer.NormalizeUnicode(composed)), TextNormalizer.Sha256Hex(TextNormalizer.NormalizeUnicode(decomposed)));
    }

    [Fact]
    public void SplitSentences_RecognisesNonLatinCapitalsAndCollapsesWhitespace()
    {
        var sentences = TextNormalizer.SplitSentences("Напряжение 55 В.     Это норма. Next one.");
        Assert.Equal(["Напряжение 55 В.", "Это норма.", "Next one."], sentences);
    }

    [Fact]
    public void TruncateGraphemes_DoesNotSplitSurrogatePairs()
    {
        var text = "ab\U0001F600cd"; // the emoji is two UTF-16 code units
        Assert.Equal("ab…", TextNormalizer.TruncateGraphemes(text, 4, "…")); // the pair does not fit, so it is dropped whole
        Assert.Equal("ab\U0001F600…", TextNormalizer.TruncateGraphemes(text, 5, "…"));
        Assert.Equal("abc", TextNormalizer.TruncateGraphemes("abc", 10));
    }
}
