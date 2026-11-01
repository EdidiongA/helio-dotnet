using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helio.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helio.IntegrationTests;

/// <summary>
/// The in-memory index is derived state. A second host started against the same database must answer
/// the same questions without any ingest call, and must do so from the vectors stored at ingest time
/// rather than by re-embedding the corpus.
/// </summary>
public class RestartTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Index_IsRebuiltFromStoredVectorsAfterRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"helio-restart-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new RestartFactory(dbPath, embedder: null))
            {
                using var op = first.CreateClient();
                op.DefaultRequestHeaders.Add("X-Api-Key", HelioApiFactory.OperatorKey);
                var ingest = await op.PostAsJsonAsync(new Uri("/api/documents", UriKind.Relative), new
                {
                    sourceId = "restart-doc",
                    title = "Restart",
                    text = "The recommended bulk charge voltage for a 16S lithium iron phosphate bank is 55.2 to 56.0 V. Float should be set at 53.6 V or disabled entirely because lithium cells do not need a float stage.",
                });
                Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
            }

            // Second host: a counting embedder proves warm-up reused the persisted vectors.
            var counting = new CountingEmbedder();
            using var second = new RestartFactory(dbPath, counting);
            using var reader = second.CreateClient();
            reader.DefaultRequestHeaders.Add("X-Api-Key", HelioApiFactory.ReaderKey);
            var ask = await reader.PostAsJsonAsync(new Uri("/api/ask", UriKind.Relative), new { question = "What bulk charge voltage should I set for a 16S lithium iron phosphate bank?" });
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var answer = await ask.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.False(answer.GetProperty("abstained").GetBoolean());
            Assert.StartsWith("restart-doc::v1::", answer.GetProperty("citations")[0].GetProperty("chunkId").GetString(), StringComparison.Ordinal);

            // Exactly one embedding call: the question. Zero chunks were re-embedded at start-up.
            Assert.Equal(1, counting.Calls);
            Assert.Equal(1, counting.TextsEmbedded);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    private sealed class RestartFactory(string dbPath, ITextEmbedder? embedder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ConnectionString"] = $"Data Source={dbPath}",
                ["Security:ReaderApiKeys:0"] = HelioApiFactory.ReaderKey,
                ["Security:OperatorApiKeys:0"] = HelioApiFactory.OperatorKey,
                ["Rag:AnswerCacheTtl"] = "00:00:00",
                ["Serilog:MinimumLevel:Default"] = "Warning",
            }));
            if (embedder is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(embedder));
            }
        }
    }

    /// <summary>Same vectors as the production stand-in (so stored ones match), but it counts every call.</summary>
    private sealed class CountingEmbedder : ITextEmbedder
    {
        private readonly Helio.Infrastructure.Ai.HashingEmbedder _inner = new();
        private int _calls;
        private int _texts;

        public int Calls => _calls;

        public int TextsEmbedded => _texts;

        public string ModelVersion => _inner.ModelVersion;

        public int Dimensions => _inner.Dimensions;

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _texts, texts.Count);
            return _inner.EmbedAsync(texts, cancellationToken);
        }
    }
}
