using System.ClientModel;
using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Helio.Application;
using Helio.Application.Answering;
using Helio.Application.Evaluation;
using Helio.Application.Ingestion;
using Helio.Application.Text;
using Helio.Domain;
using Helio.Infrastructure.Ai;
using Helio.Infrastructure.Caching;
using Helio.Infrastructure.Persistence;
using Helio.Infrastructure.Retrieval;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helio.Infrastructure;

/// <summary>Provider selection, bound from the <c>Ai</c> configuration section.</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary><c>deterministic</c> (offline, default) or <c>azure-openai</c>. Anything else is a start-up error, never a silent fallback.</summary>
    [Required, RegularExpression("^(deterministic|azure-openai)$", ErrorMessage = "Ai:Provider must be 'deterministic' or 'azure-openai'.")]
    public string Provider { get; init; } = "deterministic";

    /// <summary>The deterministic provider is a test stand-in; Production refuses it unless this is set explicitly.</summary>
    public bool AllowDeterministicInProduction { get; init; }

    public string? Endpoint { get; init; }

    /// <summary>Prefer managed identity in Azure; the key is for local development only.</summary>
    public string? ApiKey { get; init; }

    public string ChatDeployment { get; init; } = "gpt-4o-mini";

    public string EmbeddingDeployment { get; init; } = "text-embedding-3-small";

    [Range(16, 4096)]
    public int EmbeddingDimensions { get; init; } = 1536;

    /// <summary>Model prices as "inputUsdPerMillion/outputUsdPerMillion", keyed by model version.</summary>
    public Dictionary<string, string> Prices { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool UsesAzureOpenAi => string.Equals(Provider, "azure-openai", StringComparison.OrdinalIgnoreCase);
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary><c>sqlite</c> or <c>sqlserver</c>.</summary>
    [Required, RegularExpression("^(sqlite|sqlserver)$", ErrorMessage = "Storage:Database must be 'sqlite' or 'sqlserver'.")]
    public string Database { get; init; } = "sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=data/helio.db";

    /// <summary><c>memory</c> or <c>azure-ai-search</c>.</summary>
    [Required, RegularExpression("^(memory|azure-ai-search)$", ErrorMessage = "Storage:VectorStore must be 'memory' or 'azure-ai-search'.")]
    public string VectorStore { get; init; } = "memory";

    /// <summary>The in-memory store is per process; set this to run it deliberately behind a shared cache (single replica only).</summary>
    public bool AllowMemoryStoreWithRedis { get; init; }

    /// <summary>Bound on the in-process distributed cache so cached answers cannot grow without limit.</summary>
    [Range(1, 1_000_000)]
    public long MemoryCacheSizeLimitEntries { get; init; } = 10_000;

    public string? SearchEndpoint { get; init; }

    public string? SearchApiKey { get; init; }

    public string SearchIndexName { get; init; } = "helio-chunks";

    /// <summary>Redis connection string. When empty, the in-process distributed cache is used.</summary>
    public string? RedisConnectionString { get; init; }

    public bool UsesSqlServer => string.Equals(Database, "sqlserver", StringComparison.OrdinalIgnoreCase);

    public bool UsesAzureAiSearch => string.Equals(VectorStore, "azure-ai-search", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Composition root. Every provider decision is made lazily from <see cref="IOptions{TOptions}"/> at
/// resolution time rather than by reading <see cref="IConfiguration"/> during registration, so
/// configuration layered on later (test hosts, orchestrator-injected environment) is honoured.
/// </summary>
public static partial class DependencyInjection
{
    public static IServiceCollection AddHelio(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RagOptions>().Bind(configuration.GetSection(RagOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => !o.UsesAzureOpenAi || !string.IsNullOrWhiteSpace(o.Endpoint), "Ai:Endpoint is required when Ai:Provider is azure-openai.")
            .ValidateOnStart();
        services.AddOptions<AiOptions>()
            .Validate<IHostEnvironment>((o, env) => !env.IsProduction() || o.UsesAzureOpenAi || o.AllowDeterministicInProduction,
                "Production must not run the deterministic provider; set Ai:Provider=azure-openai or Ai:AllowDeterministicInProduction=true deliberately.");
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => !o.UsesAzureAiSearch || !string.IsNullOrWhiteSpace(o.SearchEndpoint), "Storage:SearchEndpoint is required when Storage:VectorStore is azure-ai-search.")
            .Validate(o => o.UsesAzureAiSearch || string.IsNullOrWhiteSpace(o.RedisConnectionString) || o.AllowMemoryStoreWithRedis,
                "Storage:VectorStore=memory is per process; with Redis configured you are almost certainly running replicas. Use azure-ai-search or set Storage:AllowMemoryStoreWithRedis=true for a single replica.")
            .ValidateOnStart();
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.SectionName)).ValidateOnStart();

        // Application services.
        services.AddSingleton<IChunker>(new ParagraphChunker());
        services.AddScoped<IngestDocumentHandler>();
        services.AddScoped<AskQuestionHandler>();
        services.AddScoped<EvaluationRunner>();
        services.AddSingleton<IAnswerJudge, LexicalAnswerJudge>();
        services.AddSingleton<ICostModel>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ConfiguredCostModel>>();
            return ConfiguredCostModel.FromDictionary(sp.GetRequiredService<IOptions<AiOptions>>().Value.Prices, model => LogUnknownPrice(logger, model));
        });

        // Persistence.
        services.AddDbContext<HelioDbContext>((sp, options) =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (storage.UsesSqlServer)
            {
                options.UseSqlServer(storage.ConnectionString, sql => sql.EnableRetryOnFailure(3));
            }
            else
            {
                EnsureSqliteDirectory(storage.ConnectionString);
                options.UseSqlite(storage.ConnectionString);
            }
        });
        services.AddScoped<IDocumentRepository, EfDocumentRepository>();
        services.AddScoped<IQueryRecordRepository, EfQueryRecordRepository>();

        // Cache: Redis when configured, otherwise in-process. Both implement IDistributedCache.
        services.AddSingleton<IDistributedCache>(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (string.IsNullOrWhiteSpace(storage.RedisConnectionString))
            {
                // Entries are counted as size 1 each, so the limit is a number of cached answers.
                return new MemoryDistributedCache(Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions { SizeLimit = storage.MemoryCacheSizeLimitEntries }));
            }

            return new RedisCache(Options.Create(new RedisCacheOptions { Configuration = storage.RedisConnectionString, InstanceName = "helio:" }));
        });
        services.AddSingleton<IAnswerCache, DistributedAnswerCache>();
        services.AddSingleton<IIndexGeneration, DistributedIndexGeneration>();

        // AI providers.
        services.AddSingleton(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return string.IsNullOrWhiteSpace(ai.ApiKey)
                ? new AzureOpenAIClient(new Uri(ai.Endpoint!), new DefaultAzureCredential())
                : new AzureOpenAIClient(new Uri(ai.Endpoint!), new ApiKeyCredential(ai.ApiKey));
        });
        services.AddSingleton<ITextEmbedder>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            if (!ai.UsesAzureOpenAi)
            {
                return new HashingEmbedder();
            }

            var generator = sp.GetRequiredService<AzureOpenAIClient>().GetEmbeddingClient(ai.EmbeddingDeployment).AsIEmbeddingGenerator();
            return new EmbeddingGeneratorEmbedder(generator, ai.EmbeddingDeployment, ai.EmbeddingDimensions);
        });
        services.AddSingleton<IAnswerGenerator>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            if (!ai.UsesAzureOpenAi)
            {
                return new ExtractiveAnswerGenerator();
            }

            IChatClient chat = sp.GetRequiredService<AzureOpenAIClient>().GetChatClient(ai.ChatDeployment).AsIChatClient();
            return new ChatClientAnswerGenerator(chat, ai.ChatDeployment);
        });

        // Vector store.
        services.AddSingleton<IVectorStore>(sp =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (!storage.UsesAzureAiSearch)
            {
                return new InMemoryHybridStore();
            }

            var indexClient = string.IsNullOrWhiteSpace(storage.SearchApiKey)
                ? new SearchIndexClient(new Uri(storage.SearchEndpoint!), new DefaultAzureCredential())
                : new SearchIndexClient(new Uri(storage.SearchEndpoint!), new AzureKeyCredential(storage.SearchApiKey));
            return new AzureAiSearchStore(indexClient, storage.SearchIndexName, sp.GetRequiredService<ITextEmbedder>().Dimensions);
        });
        services.AddHostedService<IndexWarmupService>();
        services.AddHostedService<QueryRecordRetentionService>();
        services.AddHealthChecks().AddCheck<ReadinessCheck>("readiness", tags: ["ready"]);

        return services;
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "No price configured for model version {ModelVersion}; its spend is being recorded as zero")]
    private static partial void LogUnknownPrice(ILogger logger, string modelVersion);

    /// <summary>SQLite does not create missing directories; a first run on a clean volume would otherwise fail with "unable to open database file".</summary>
    private static void EnsureSqliteDirectory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

/// <summary>
/// Ready only when the database answers and the vector index is reachable. Results are cached briefly
/// so a probe storm (or a public hit on the readiness URL) does not become a storm on the backends.
/// </summary>
public sealed class ReadinessCheck(IServiceProvider services, TimeProvider? timeProvider = null) : IHealthCheck, IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult? _last;
    private DateTimeOffset _lastAt;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (_last is { } cached && now - _lastAt < CacheFor)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (_last is { } again && now - _lastAt < CacheFor)
            {
                return again;
            }

            var result = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _last = result;
            _lastAt = now;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelioDbContext>();
        if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return HealthCheckResult.Unhealthy("Database unreachable.");
        }

        try
        {
            var chunks = await scope.ServiceProvider.GetRequiredService<IVectorStore>().CountAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"{chunks} chunks indexed.", new Dictionary<string, object> { ["chunks"] = chunks });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never echo provider error text (hostnames, request ids) to an anonymous probe caller.
            return HealthCheckResult.Unhealthy("Vector index unreachable.");
        }
    }
}
