using System.Globalization;
using System.Text;
using System.Text.Json;
using Helio.Application.Answering;
using Helio.Application.Evaluation;
using Helio.Application.Ingestion;
using Helio.Domain;
using Helio.Infrastructure;
using Helio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage: dotnet run --project evals/Helio.Evaluation -- [corpusDir] [goldenJson] [reportPath]
// Exit code 0 when every gate passes, 1 otherwise. Runs fully offline with the deterministic provider.

var corpusDir = args.Length > 0 ? args[0] : "corpus";
var goldenPath = args.Length > 1 ? args[1] : Path.Combine("evals", "golden", "golden.json");
var reportPath = args.Length > 2 ? args[2] : Path.Combine("evals", "report.json");

// The evaluation harness is a test tool: it runs as the "Evaluation" environment so the Production-only
// guards (no deterministic provider, pepper required) do not apply, and nothing here is reachable over HTTP.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Evaluation" });
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Storage:ConnectionString"] = "Data Source=:memory:",
    ["Rag:AnswerCacheTtl"] = "00:00:00",
});
builder.Services.AddHelio(builder.Configuration);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var provider = scope.ServiceProvider;

// Keep the in-memory SQLite connection open for the life of the run.
var db = provider.GetRequiredService<HelioDbContext>();
await db.Database.OpenConnectionAsync().ConfigureAwait(false);
await db.Database.EnsureCreatedAsync().ConfigureAwait(false);

var ingest = provider.GetRequiredService<IngestDocumentHandler>();
foreach (var file in Directory.GetFiles(corpusDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
{
    var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
    var title = text.Split('\n')[0].TrimStart('#', ' ').Trim();
    await ingest.HandleAsync(new IngestRequest(Path.GetFileNameWithoutExtension(file), title, text), CancellationToken.None).ConfigureAwait(false);
}

var golden = JsonSerializer.Deserialize<GoldenCase[]>(await File.ReadAllTextAsync(goldenPath).ConfigureAwait(false), new JsonSerializerOptions(JsonSerializerDefaults.Web))
    ?? throw new InvalidOperationException("Golden set is empty.");

var runner = provider.GetRequiredService<EvaluationRunner>();
var summary = await runner.RunAsync(golden, CancellationToken.None).ConfigureAwait(false);

var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"cases: {summary.Cases}");
report.AppendLine(CultureInfo.InvariantCulture, $"accuracy: {summary.Accuracy:P1}");
report.AppendLine(CultureInfo.InvariantCulture, $"retrieval_hit_rate: {summary.RetrievalHitRate:P1}");
report.AppendLine(CultureInfo.InvariantCulture, $"mean_faithfulness: {summary.MeanFaithfulness:F3}");
report.AppendLine(CultureInfo.InvariantCulture, $"mean_answer_relevance: {summary.MeanAnswerRelevance:F3}");
report.AppendLine(CultureInfo.InvariantCulture, $"false_answers: {summary.FalseAnswers}");
report.AppendLine(CultureInfo.InvariantCulture, $"p50_ms: {summary.P50LatencyMs}  p95_ms: {summary.P95LatencyMs}");
report.AppendLine("outcomes:");
foreach (var (outcome, count) in summary.Outcomes.OrderByDescending(kv => kv.Value))
{
    report.AppendLine(CultureInfo.InvariantCulture, $"  {outcome,-18} {count}");
}

report.AppendLine("failures:");
foreach (var r in summary.Results.Where(r => r.Outcome is not (EvalOutcome.Correct or EvalOutcome.CorrectAbstention)))
{
    report.AppendLine(CultureInfo.InvariantCulture, $"  {r.CaseId} [{r.Category}] {r.Outcome}: {Truncate(r.AnswerText, 110)}");
}

Console.WriteLine(report.ToString());
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })).ConfigureAwait(false);

// Quality gates. These are the floor; a change that drops below any of them fails CI.
var gates = new (string Name, bool Pass)[]
{
    ("accuracy >= 0.85", summary.Accuracy >= 0.85),
    ("retrieval hit rate >= 0.95", summary.RetrievalHitRate >= 0.95),
    ("zero false answers", summary.FalseAnswers == 0),
    ("mean faithfulness >= 0.90", summary.MeanFaithfulness >= 0.90),
    ("p95 latency < 2000 ms", summary.P95LatencyMs < 2000),
};

var failed = false;
foreach (var (name, pass) in gates)
{
    Console.WriteLine($"gate {(pass ? "PASS" : "FAIL")}: {name}");
    failed |= !pass;
}

return failed ? 1 : 0;

static string Truncate(string s, int max) => Helio.Application.Text.TextNormalizer.TruncateGraphemes(s, max);
