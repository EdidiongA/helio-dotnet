using Helio.Api;
using Helio.Api.Endpoints;
using Helio.Api.Security;
using Helio.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: JSON to stdout, so the container platform collects it unchanged.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "helio-api"));

builder.Services.AddHelio(builder.Configuration);
builder.Services.AddHelioSecurity(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHelioRateLimiting();
builder.Services.AddSingleton<HelioMetrics>();
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((o, config) =>
{
    // Trust X-Forwarded-* only from the proxies named in configuration (ingress controller CIDRs). With an
    // empty list the defaults apply (loopback only), so a client can never spoof its own address.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    foreach (var cidr in config.GetSection("Proxy:TrustedNetworks").Get<string[]>() ?? [])
    {
        if (System.Net.IPNetwork.TryParse(cidr, out var network))
        {
            o.KnownIPNetworks.Add(network);
        }
    }
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 4 * 1024 * 1024); // per-endpoint limits tighten this further

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("helio-api", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(HelioMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after authentication so limits are keyed by principal, not by address

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public,max-age=3600",
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Operator credential required; Prometheus presents it as a bearer token (bearer_token_file), see deploy/k8s.
app.MapPrometheusScrapingEndpoint("/metrics").RequireAuthorization(Policies.Operator);
app.MapHelioEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed for the integration test host.</summary>
public partial class Program;
