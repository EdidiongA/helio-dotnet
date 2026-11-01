using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Helio.Api.Security;

public static class Policies
{
    /// <summary>May ask questions.</summary>
    public const string Reader = "reader";

    /// <summary>May ingest documents and read audit data and metrics.</summary>
    public const string Operator = "operator";
}

/// <summary>
/// Authentication: API keys for service-to-service and local use, and optional Microsoft Entra ID
/// (JWT bearer) for interactive users. Keys are compared in constant time and never logged.
/// Every security-relevant setting is validated at start-up with environment-aware rules, so an
/// insecure configuration fails closed instead of running.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public const int MinimumKeyLength = 32;

    /// <summary>Keys with the <c>reader</c> role.</summary>
    public string[] ReaderApiKeys { get; init; } = [];

    /// <summary>Keys with the <c>operator</c> role (implies reader).</summary>
    public string[] OperatorApiKeys { get; init; } = [];

    /// <summary>When true, anonymous callers get the reader role. Development only; refused in Production.</summary>
    public bool AllowAnonymousReads { get; init; }

    /// <summary>Entra ID authority, for example https://login.microsoftonline.com/{tenant}/v2.0. Empty disables JWT.</summary>
    public string? JwtAuthority { get; init; }

    /// <summary>Required whenever <see cref="JwtAuthority"/> is set: without it any token from the tenant would be accepted.</summary>
    public string? JwtAudience { get; init; }

    /// <summary>Entra app role value that maps to the reader role.</summary>
    public string JwtReaderRole { get; init; } = "Helio.Reader";

    /// <summary>Entra app role value that maps to the operator role.</summary>
    public string JwtOperatorRole { get; init; } = "Helio.Operator";

    /// <summary>Server-side secret for pseudonymising caller identities in audit records (HMAC key). Required in Production.</summary>
    public string? CallerHashPepper { get; init; }

    /// <summary>Sustained requests per minute per caller for <c>/api/ask</c>.</summary>
    public int AskPermitPerMinute { get; init; } = 60;

    /// <summary>Requests per minute per caller for ingestion.</summary>
    public int IngestPermitPerMinute { get; init; } = 10;

    /// <summary>Golden cases per ten minutes per operator for <c>/api/evaluate</c>; budgets by work done, not by request.</summary>
    public int EvaluateCasesPerTenMinutes { get; init; } = 200;

    /// <summary>Requests per minute per client address for callers that present no valid credential (401/403 storms, key guessing).</summary>
    public int UnauthenticatedPermitPerMinute { get; init; } = 30;

    public bool JwtEnabled => !string.IsNullOrWhiteSpace(JwtAuthority);

    public IEnumerable<string> AllKeys => ReaderApiKeys.Concat(OperatorApiKeys).Where(k => !string.IsNullOrEmpty(k));
}

public static class SecuritySetup
{
    public const string ApiKeyScheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Name of the policy for anonymous-denied requests; see <see cref="AddHelioRateLimiting"/>.</summary>
    private const string AnonymousSubject = "anonymous";

    public static IServiceCollection AddHelioSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .Validate(o => !o.JwtEnabled || !string.IsNullOrWhiteSpace(o.JwtAudience), "Security:JwtAudience is required when Security:JwtAuthority is set.")
            .Validate(o => o.AllKeys.All(k => !k.Contains("change-me", StringComparison.OrdinalIgnoreCase)), "Security: an API key still carries a placeholder value.")
            .ValidateOnStart();
        services.AddOptions<SecurityOptions>()
            .Validate<IHostEnvironment>((o, env) => !env.IsProduction() || !o.AllowAnonymousReads, "Security:AllowAnonymousReads is not permitted in Production.")
            .Validate<IHostEnvironment>((o, env) => !env.IsProduction() || o.AllKeys.All(k => k.Length >= SecurityOptions.MinimumKeyLength), $"Security: API keys must be at least {SecurityOptions.MinimumKeyLength} characters in Production.")
            .Validate<IHostEnvironment>((o, env) => !env.IsProduction() || o.AllKeys.Any() || o.JwtEnabled, "Security: Production has no credential source (no API keys and no JWT authority).")
            .Validate<IHostEnvironment>((o, env) => !env.IsProduction() || !string.IsNullOrWhiteSpace(o.CallerHashPepper), "Security:CallerHashPepper is required in Production so audit pseudonyms cannot be reversed from public identifiers.");

        // JWT is always registered; it is inert until an authority is configured. Registration therefore no
        // longer depends on configuration read at start-up, and options layered on later are honoured.
        services.AddAuthentication(ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyScheme, null)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Reader, p =>
            {
                p.AddAuthenticationSchemes(ApiKeyScheme, JwtBearerDefaults.AuthenticationScheme);
                p.RequireAuthenticatedUser();
                p.AddRequirements(new RoleRequirement(Policies.Reader, Policies.Operator, o => [o.JwtReaderRole, o.JwtOperatorRole]));
            })
            .AddPolicy(Policies.Operator, p =>
            {
                p.AddAuthenticationSchemes(ApiKeyScheme, JwtBearerDefaults.AuthenticationScheme);
                p.RequireAuthenticatedUser();
                p.AddRequirements(new RoleRequirement(Policies.Operator, null, o => [o.JwtOperatorRole]));
            });
        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();

        return services;
    }

    /// <summary>Limits are read from <see cref="IOptions{TOptions}"/> per partition so configuration layered on after registration is honoured.</summary>
    public static IServiceCollection AddHelioRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            // Callers without a valid principal are throttled per address, so key guessing is bounded at the
            // application layer and not only at the ingress.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.User.Identity?.IsAuthenticated == true
                    ? RateLimitPartition.GetNoLimiter("authenticated")
                    : RateLimitPartition.GetFixedWindowLimiter("unauth:" + ClientAddress(ctx), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Limits(ctx).UnauthenticatedPermitPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            limiter.AddPolicy("ask", ctx => RateLimitPartition.GetSlidingWindowLimiter(CallerKey(ctx), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Limits(ctx).AskPermitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            }));
            limiter.AddPolicy("ingest", ctx => RateLimitPartition.GetFixedWindowLimiter(CallerKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Limits(ctx).IngestPermitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
            limiter.AddPolicy("evaluate", ctx => RateLimitPartition.GetTokenBucketLimiter(CallerKey(ctx), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = Limits(ctx).EvaluateCasesPerTenMinutes,
                TokensPerPeriod = Limits(ctx).EvaluateCasesPerTenMinutes,
                ReplenishmentPeriod = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
        });
        return services;
    }

    private static SecurityOptions Limits(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;

    private static string ClientAddress(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Rate limit per authenticated principal; anonymous readers are keyed by client address so one client cannot exhaust everyone's budget.</summary>
    internal static string CallerKey(HttpContext ctx)
    {
        var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is not null && id != AnonymousSubject ? id : "ip:" + ClientAddress(ctx);
    }

    /// <summary>
    /// Stable pseudonym for a principal, safe to store in audit records. Keyed (HMAC) when a pepper is
    /// configured so a known Entra object id cannot be hashed by an outsider to look up someone's questions.
    /// </summary>
    public static string CallerHash(ClaimsPrincipal user, SecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var id = user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AnonymousSubject;
        var bytes = Encoding.UTF8.GetBytes(id);
        var digest = string.IsNullOrEmpty(options.CallerHashPepper)
            ? SHA256.HashData(bytes)
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.CallerHashPepper), bytes);
        return Convert.ToHexStringLower(digest)[..32];
    }

    /// <summary>Configured lazily from <see cref="IOptions{TOptions}"/>, so the test host's configuration overrides apply.</summary>
    private sealed class ConfigureJwtBearer(IOptions<SecurityOptions> security) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(string? name, JwtBearerOptions options)
        {
            var s = security.Value;
            if (!s.JwtEnabled)
            {
                return;
            }

            options.Authority = s.JwtAuthority;
            options.MapInboundClaims = false; // keep "roles" and "oid" under their wire names
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudiences = [s.JwtAudience!, $"api://{s.JwtAudience}"];
            options.TokenValidationParameters.RoleClaimType = "roles";
            options.TokenValidationParameters.NameClaimType = "oid";
        }

        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);
    }

    /// <summary>Role check whose Entra role names come from options at evaluation time rather than from configuration captured at registration.</summary>
    private sealed class RoleRequirement(string primary, string? implied, Func<SecurityOptions, string[]> jwtRoles) : IAuthorizationRequirement
    {
        public string Primary { get; } = primary;

        public string? Implied { get; } = implied;

        public Func<SecurityOptions, string[]> JwtRoles { get; } = jwtRoles;
    }

    private sealed class RoleRequirementHandler(IOptions<SecurityOptions> security) : AuthorizationHandler<RoleRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
        {
            var accepted = new HashSet<string>(StringComparer.Ordinal) { requirement.Primary };
            if (requirement.Implied is not null)
            {
                accepted.Add(requirement.Implied);
            }

            accepted.UnionWith(requirement.JwtRoles(security.Value));
            var roles = context.User.FindAll(ClaimTypes.Role).Concat(context.User.FindAll("roles")).Select(c => c.Value);
            if (roles.Any(accepted.Contains))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>Validates <c>X-Api-Key</c> (or <c>Authorization: Bearer</c> for scrapers that cannot send custom headers) against configured keys using constant-time comparison.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SecurityOptions> security) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string AnonymousSubject = "anonymous";
    private readonly SecurityOptions _security = security.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Presented();
        if (presented is null)
        {
            if (_security.AllowAnonymousReads)
            {
                return Task.FromResult(AuthenticateResult.Success(Ticket(AnonymousSubject, Policies.Reader)));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (Matches(presented, _security.OperatorApiKeys))
        {
            return Task.FromResult(AuthenticateResult.Success(Ticket("key:" + Fingerprint(presented), Policies.Operator, Policies.Reader)));
        }

        if (Matches(presented, _security.ReaderApiKeys))
        {
            return Task.FromResult(AuthenticateResult.Success(Ticket("key:" + Fingerprint(presented), Policies.Reader)));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    private string? Presented()
    {
        if (Request.Headers.TryGetValue(SecuritySetup.HeaderName, out var values) && values.Count > 0)
        {
            return values[0] ?? string.Empty;
        }

        // A bearer token that is not a JWT (no dots) is treated as an API key, which lets Prometheus use bearer_token_file.
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            if (token.Length > 0 && !token.Contains('.', StringComparison.Ordinal))
            {
                return token;
            }
        }

        return null;
    }

    private AuthenticationTicket Ticket(string subject, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.NameIdentifier, ClaimTypes.Role);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
    }

    private static bool Matches(string presented, string[] configured)
    {
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        foreach (var key in configured)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var keyBytes = Encoding.UTF8.GetBytes(key);
            if (keyBytes.Length == presentedBytes.Length && CryptographicOperations.FixedTimeEquals(keyBytes, presentedBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static string Fingerprint(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
}

public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        await next().ConfigureAwait(false);
    });
}
