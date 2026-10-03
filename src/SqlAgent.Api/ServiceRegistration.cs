using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SqlAgent.Api.Endpoints;
using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;

namespace SqlAgent.Api;

/// <summary>
/// Wiring of the agent and the API. Everything that needs the database or the model is built on first use, from
/// settings read at that moment, so tests can replace those pieces (the chat client, the catalog, the executor and the
/// audit sink) and host the HTTP layer with none of them.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddSqlAgentCore(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ApiSettings(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(new AgentAssetsOptions());
        services.AddSingleton(new SqlGuardrail());
        services.AddSingleton(SseOptions.Default);

        services.AddSingleton<ISchemaCatalog>(sp =>
            SemanticLayerLoader.LoadAsync(sp.GetRequiredService<AgentAssetsOptions>(), sp.GetRequiredService<ApiSettings>().AppConnectionString)
                .GetAwaiter().GetResult());
        services.AddSingleton<IQueryExecutor>(sp => new SafeQueryExecutor(
            sp.GetRequiredService<ApiSettings>().CreateRoleConnections(),
            logger: sp.GetRequiredService<ILogger<SafeQueryExecutor>>()));
        services.AddSingleton<IAuditSink>(sp => new SqlAuditSink(
            sp.GetRequiredService<ApiSettings>().AppConnectionString, sp.GetRequiredService<ILogger<SqlAuditSink>>()));

        services.AddSingleton(sp => sp.GetRequiredService<ApiSettings>().Llm);
        services.AddSingleton(sp => AgentOptions.For(
            sp.GetRequiredService<LlmOptions>(), sp.GetRequiredService<ApiSettings>().ResultVisibility));
        services.AddSingleton<IChatClient>(sp =>
            ChatClientFactory.Create(sp.GetRequiredService<LlmOptions>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(CreateRegistry);

        services.AddSingleton(new ConversationStore());
        services.AddSingleton(sp => new RunSlots(sp.GetRequiredService<ApiSettings>().MaxConcurrentRuns));
        services.AddSingleton<AgentTurnRunner>();
        return services;
    }

    private static RoleAgentRegistry CreateRegistry(IServiceProvider sp) => RoleAgentRegistry.Create(
        sp.GetRequiredService<IChatClient>(),
        sp.GetRequiredService<ISchemaCatalog>(),
        sp.GetRequiredService<AgentAssetsOptions>(),
        sp.GetRequiredService<LlmOptions>(),
        sp.GetRequiredService<ILoggerFactory>());

    public static IServiceCollection AddSqlAgentSecurity(this IServiceCollection services)
    {
        // The token and the limits are configured lazily, from the settings, so a missing key fails at startup
        // (see Program) rather than at the first request.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ApiSettings>((options, settings) =>
            {
                options.MapInboundClaims = false; // keep "sub" and "role" as they are in the token
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = ApiSettings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = ApiSettings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = AuthEndpoints.SigningKey(settings.JwtSigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
        services.AddAuthorization();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<ApiSettings>((options, settings) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Slow down." }, ct);
            };

            // Per user: the token's sub. Per address for the token endpoint, which has no user yet.
            options.AddPolicy(ChatEndpoints.ChatRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst("sub")?.Value ?? "anonymous",
                _ => PerMinute(settings.ChatRequestsPerMinute)));
            options.AddPolicy(AuthEndpoints.DemoTokenRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => PerMinute(settings.DemoTokenRequestsPerMinute)));
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions PerMinute(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };

    /// <summary>
    /// Traces and metrics for ASP.NET Core, outgoing HTTP, SQL Server and the chat client, sent over OTLP when an
    /// endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, the Aspire Dashboard in the local stack).
    /// Prompt and completion text is not recorded: it can contain query results.
    /// </summary>
    public static IServiceCollection AddSqlAgentTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var export = !string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(configuration["OTEL_SERVICE_NAME"] ?? "sqlagent-api"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o => o.Filter = http => http.Request.Path != "/healthz")
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation()
                    .AddSource(ChatClientFactory.TelemetrySourceName, AgentTelemetry.SourceName);
                if (export) tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(AgentTelemetry.MeterName, ChatClientFactory.TelemetrySourceName);
                if (export) metrics.AddOtlpExporter();
            });
        return services;
    }
}
