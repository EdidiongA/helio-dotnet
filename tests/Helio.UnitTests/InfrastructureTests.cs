using Helio.Application.Answering;
using Helio.Application.Evaluation;
using Helio.Domain;
using Helio.Infrastructure.Ai;
using Helio.Infrastructure.Retrieval;

namespace Helio.UnitTests;

public class HashingEmbedderTests
{
    [Fact]
    public async Task Embed_IsDeterministicAndUnitLength()
    {
        var embedder = new HashingEmbedder(128);
        var a = await embedder.EmbedAsync(["Charge the battery to 55.2 V"], CancellationToken.None);
        var b = await embedder.EmbedAsync(["Charge the battery to 55.2 V"], CancellationToken.None);
        Assert.Equal(a[0].ToArray(), b[0].ToArray());
        var norm = Math.Sqrt(a[0].ToArray().Sum(x => (double)x * x));
        Assert.InRange(norm, 0.999, 1.001);
    }

    [Fact]
    public async Task Embed_SimilarTextIsCloserThanUnrelatedText()
    {
        var embedder = new HashingEmbedder();
        var v = await embedder.EmbedAsync(["lithium battery charge voltage", "battery charging voltage for lithium", "football world cup final"], CancellationToken.None);
        static double Dot(ReadOnlyMemory<float> x, ReadOnlyMemory<float> y) => x.Span.ToArray().Zip(y.Span.ToArray(), (p, q) => (double)p * q).Sum();
        Assert.True(Dot(v[0], v[1]) > Dot(v[0], v[2]));
    }

    [Fact]
    public void StableHash_DoesNotDependOnProcess()
    {
        Assert.Equal(HashingEmbedder.StableHash("helio"), HashingEmbedder.StableHash("helio"));
        Assert.NotEqual(HashingEmbedder.StableHash("helio"), HashingEmbedder.StableHash("heli0"));
    }
}

public class InMemoryHybridStoreTests
{
    private static Chunk MakeChunk(string source, int ordinal, string text) =>
        new() { Id = Chunk.MakeId(source, 1, ordinal), DocumentId = Guid.NewGuid(), SourceId = source, Title = source, Ordinal = ordinal, Text = text };

    private static async Task<InMemoryHybridStore> SeededStoreAsync(HashingEmbedder embedder)
    {
        var store = new InMemoryHybridStore();
        var chunks = new[]
        {
            MakeChunk("battery", 0, "The recommended bulk charge voltage for a 16S lithium bank is 55.2 to 56.0 V."),
            MakeChunk("battery", 1, "Float should be set at 53.6 to 54.0 V or disabled entirely."),
            MakeChunk("wiring", 0, "Use a Class T fuse within 150 mm of the positive terminal."),
            MakeChunk("sport", 0, "The football match ended in a draw after extra time."),
        };
        var embeddings = await embedder.EmbedAsync(chunks.Select(c => c.Text).ToArray(), CancellationToken.None);
        await store.UpsertAsync(chunks, embeddings, CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task Search_RanksLexicallyAndSemanticallyRelevantChunkFirst()
    {
        var embedder = new HashingEmbedder();
        var store = await SeededStoreAsync(embedder);
        var query = "What bulk charge voltage for a 16S bank?";
        var results = await store.SearchAsync(query, (await embedder.EmbedAsync([query], CancellationToken.None))[0], 3, CancellationToken.None);

        Assert.Equal("battery::v1::p0", results[0].Chunk.Id);
        Assert.All(results.Skip(1), r => Assert.True(r.Score <= results[0].Score));
        Assert.DoesNotContain(results, r => r.Chunk.SourceId == "sport");
        Assert.Equal("hybrid-rrf", results[0].Retriever);
    }

    [Fact]
    public async Task Search_ReturnsNothingForEmptyStoreOrNonPositiveTopK()
    {
        var store = new InMemoryHybridStore();
        Assert.Empty(await store.SearchAsync("x", new float[8], 3, CancellationToken.None));
        var embedder = new HashingEmbedder();
        var seeded = await SeededStoreAsync(embedder);
        Assert.Empty(await seeded.SearchAsync("x", new float[512], 0, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteBySource_RemovesOnlyThatSource()
    {
        var embedder = new HashingEmbedder();
        var store = await SeededStoreAsync(embedder);
        await store.DeleteBySourceAsync("battery", CancellationToken.None);
        Assert.Equal(2, await store.CountAsync(CancellationToken.None));
        var results = await store.SearchAsync("fuse positive terminal", (await embedder.EmbedAsync(["fuse"], CancellationToken.None))[0], 5, CancellationToken.None);
        Assert.DoesNotContain(results, r => r.Chunk.SourceId == "battery");
    }

    [Fact]
    public async Task Upsert_RejectsMismatchedEmbeddingCount()
    {
        var store = new InMemoryHybridStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync([MakeChunk("a", 0, "x")], [], CancellationToken.None));
    }

    [Fact]
    public async Task Search_IsSafeUnderConcurrentUpserts()
    {
        var embedder = new HashingEmbedder();
        var store = await SeededStoreAsync(embedder);
        var queryVector = (await embedder.EmbedAsync(["voltage"], CancellationToken.None))[0];
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var chunk = MakeChunk("stream", i, $"Streamed chunk number {i} about voltage and charge.");
                await store.UpsertAsync([chunk], await embedder.EmbedAsync([chunk.Text], CancellationToken.None), CancellationToken.None);
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var results = await store.SearchAsync("voltage", queryVector, 5, CancellationToken.None);
                Assert.NotEmpty(results);
            }
        });
        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public async Task Search_IsSafeWhileASourceIsDeletedAndReIngested()
    {
        // Regression for the race where fusion looked ids up in the live dictionary after a concurrent delete.
        var embedder = new HashingEmbedder();
        var store = await SeededStoreAsync(embedder);
        var queryVector = (await embedder.EmbedAsync(["bulk charge voltage"], CancellationToken.None))[0];
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await store.DeleteBySourceAsync("battery", CancellationToken.None);
                var chunk = MakeChunk("battery", 0, $"Bulk charge voltage revision {i}: 55.2 V.");
                await store.UpsertAsync([chunk], await embedder.EmbedAsync([chunk.Text], CancellationToken.None), CancellationToken.None);
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var results = await store.SearchAsync("bulk charge voltage", queryVector, 5, CancellationToken.None);
                Assert.All(results, r => Assert.NotNull(r.Chunk));
            }
        });
        await Task.WhenAll(writer, reader);
        Assert.Equal(3, await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Statistics_AreMaintainedIncrementally()
    {
        var embedder = new HashingEmbedder();
        var store = await SeededStoreAsync(embedder);
        var before = await store.SearchAsync("football draw", (await embedder.EmbedAsync(["football"], CancellationToken.None))[0], 1, CancellationToken.None);
        Assert.Equal("sport::v1::p0", before[0].Chunk.Id);
        await store.DeleteBySourceAsync("sport", CancellationToken.None);
        var after = await store.SearchAsync("football draw", (await embedder.EmbedAsync(["football"], CancellationToken.None))[0], 3, CancellationToken.None);
        Assert.DoesNotContain(after, r => r.Chunk.SourceId == "sport");
        // Re-adding restores the exact ranking, so the incremental df/avgLen bookkeeping is consistent.
        var chunk = MakeChunk("sport", 0, "The football match ended in a draw after extra time.");
        await store.UpsertAsync([chunk], await embedder.EmbedAsync([chunk.Text], CancellationToken.None), CancellationToken.None);
        var restored = await store.SearchAsync("football draw", (await embedder.EmbedAsync(["football"], CancellationToken.None))[0], 1, CancellationToken.None);
        Assert.Equal(before[0].Score, restored[0].Score);
    }
}

public class ExtractiveAnswerGeneratorTests
{
    private static RetrievedChunk Ctx(string id, string text) =>
        new(new Chunk { Id = id, DocumentId = Guid.NewGuid(), SourceId = id.Split("::")[0], Title = "T", Ordinal = 0, Text = text }, 1, "t");

    [Fact]
    public async Task Generate_AnswersFromBestSupportedSentencesAndCitesChunk()
    {
        var generator = new ExtractiveAnswerGenerator();
        var context = new[]
        {
            Ctx("bat::v1::p0", "A 48 V bank has 16 cells. The recommended bulk charge voltage for a 16S bank is 55.2 to 56.0 V. Float should be 53.6 V."),
            Ctx("wire::v1::p0", "Use 35 mm2 copper cable for short runs."),
        };

        var result = await generator.GenerateAsync("What bulk charge voltage should I set for a 16S bank?", context, CancellationToken.None);

        Assert.NotNull(result.Draft);
        Assert.False(result.Draft.Abstained);
        Assert.Contains("55.2 to 56.0 V", result.Draft.Answer, StringComparison.Ordinal);
        Assert.Equal(["bat::v1::p0"], result.Draft.CitationChunkIds);
        Assert.True(result.InputTokens > 0);
    }

    [Fact]
    public async Task Generate_AbstainsWhenQuestionIsNotCovered()
    {
        var generator = new ExtractiveAnswerGenerator();
        var result = await generator.GenerateAsync("Who won the World Cup?", [Ctx("bat::v1::p0", "The recommended bulk charge voltage is 55.2 V.")], CancellationToken.None);
        Assert.NotNull(result.Draft);
        Assert.True(result.Draft.Abstained);
        Assert.Empty(result.Draft.CitationChunkIds);
    }

    [Fact]
    public async Task Generate_WithEmptyContextAbstains()
    {
        var result = await new ExtractiveAnswerGenerator().GenerateAsync("Anything?", [], CancellationToken.None);
        Assert.True(result.Draft!.Abstained);
    }
}

public class ConfiguredCostModelTests
{
    [Fact]
    public void EstimateMicroUsd_UsesPerMillionPrices()
    {
        var model = ConfiguredCostModel.FromDictionary(new Dictionary<string, string> { ["gpt-4o-mini"] = "0.15/0.60", ["bad"] = "oops" });
        Assert.Equal(750, model.EstimateMicroUsd("gpt-4o-mini", 1_000, 1_000)); // 0.00015 + 0.0006 USD = 750 micro USD
        Assert.Equal(0, model.EstimateMicroUsd("unknown", 1_000, 1_000));
        Assert.Equal(0, model.EstimateMicroUsd("bad", 1_000, 1_000));
    }

    [Fact]
    public void EstimateMicroUsd_ReportsAnUnknownModelOnceSoZeroSpendIsNeverSilent()
    {
        var reported = new List<string>();
        var model = ConfiguredCostModel.FromDictionary(new Dictionary<string, string>(), reported.Add);
        model.EstimateMicroUsd("mystery-deployment", 10, 10);
        model.EstimateMicroUsd("mystery-deployment", 10, 10);
        Assert.Equal(["mystery-deployment"], reported);
        Assert.Contains("mystery-deployment", model.UnknownModelVersions);
    }
}

public class VectorCodecTests
{
    [Fact]
    public void Encode_RoundTripsExactly()
    {
        float[] vector = [0.25f, -1f, 3.5e-3f, float.MaxValue];
        var decoded = Helio.Infrastructure.Persistence.VectorCodec.Decode(Helio.Infrastructure.Persistence.VectorCodec.Encode(vector));
        Assert.Equal(vector, decoded.ToArray());
    }
}

public class AzureAiSearchQueryTests
{
    [Fact]
    public void EscapeSimpleQuery_NeutralisesOperators()
    {
        Assert.Equal(@"what is \""bulk\"" \(voltage\) \+ \-float\|x\*", AzureAiSearchStore.EscapeSimpleQuery(@"what is ""bulk"" (voltage) + -float|x*"));
        Assert.Equal("plain question", AzureAiSearchStore.EscapeSimpleQuery("plain question"));
    }
}

public class EvaluationClassificationTests
{
    private static readonly RetrievedChunk Chunk = new(new Domain.Chunk { Id = "s::v1::p0", DocumentId = Guid.NewGuid(), SourceId = "s", Title = "T", Ordinal = 0, Text = "x" }, 1, "t");

    private static Answer Answered(string text, string chunkId = "s::v1::p0") => new()
    {
        Text = text,
        Citations = [new Citation(chunkId, "s", "T", "x")],
        Confidence = 0.9,
        Abstained = false,
    };

    [Fact]
    public void Classify_CoversEveryBranch()
    {
        var golden = new GoldenCase("q", "?", ["55.2"], "s", false, "factual");
        Assert.Equal(EvalOutcome.Correct, EvaluationRunner.Classify(golden, Answered("It is 55.2 V"), true, [Chunk]));
        Assert.Equal(EvalOutcome.GenerationFailure, EvaluationRunner.Classify(golden, Answered("It is 60 V"), true, [Chunk]));
        Assert.Equal(EvalOutcome.RetrievalFailure, EvaluationRunner.Classify(golden, Answered("It is 55.2 V"), false, [Chunk]));
        Assert.Equal(EvalOutcome.CitationMismatch, EvaluationRunner.Classify(golden, Answered("It is 55.2 V", "other"), true, [Chunk]));
        Assert.Equal(EvalOutcome.GenerationFailure, EvaluationRunner.Classify(golden, Answer.Abstain("x"), true, [Chunk]));

        var canary = new GoldenCase("c", "?", [], null, true, "unanswerable");
        Assert.Equal(EvalOutcome.CorrectAbstention, EvaluationRunner.Classify(canary, Answer.Abstain("x"), true, []));
        Assert.Equal(EvalOutcome.FalseAnswer, EvaluationRunner.Classify(canary, Answered("confident nonsense"), true, [Chunk]));

        var adversarial = golden with { ForbiddenPhrases = ["70 V"], AllowAbstain = true };
        Assert.Equal(EvalOutcome.FalseAnswer, EvaluationRunner.Classify(adversarial, Answered("It is 70 V"), true, [Chunk]));
        Assert.Equal(EvalOutcome.CorrectAbstention, EvaluationRunner.Classify(adversarial, Answer.Abstain("x"), true, [Chunk]));
    }

    [Fact]
    public void ContainsKeyphrases_IsCaseInsensitiveAndWhitespaceTolerant()
    {
        Assert.True(EvaluationRunner.ContainsKeyphrases("Lagos averages  4.0 to 4.5 peak sun hours", ["4.0 to 4.5"]));
        Assert.True(EvaluationRunner.ContainsKeyphrases("Use a CLASS T fuse", ["class t"]));
        Assert.False(EvaluationRunner.ContainsKeyphrases("Nothing here", ["55.2"]));
        Assert.True(EvaluationRunner.ContainsKeyphrases("anything", []));
    }

    [Fact]
    public async Task LexicalJudge_ScoresGroundedAnswersHigh()
    {
        var judge = new LexicalAnswerJudge();
        var context = new[] { Chunk with { Chunk = new Domain.Chunk { Id = "a", DocumentId = Guid.NewGuid(), SourceId = "s", Title = "T", Ordinal = 0, Text = "The bulk charge voltage is 55.2 V." } } };
        var (faithful, relevant) = await judge.JudgeAsync("What is the bulk charge voltage?", "The bulk charge voltage is 55.2 V.", context, CancellationToken.None);
        Assert.Equal(1, faithful);
        Assert.Equal(1, relevant);
        var (unfaithful, _) = await judge.JudgeAsync("?", "Penguins migrate to Antarctica every winter season.", context, CancellationToken.None);
        Assert.Equal(0, unfaithful);
    }
}
