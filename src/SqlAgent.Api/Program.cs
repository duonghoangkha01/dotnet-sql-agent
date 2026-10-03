using SqlAgent.Api;
using SqlAgent.Api.Endpoints;
using SqlAgent.Core.Agent;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddSqlAgentCore()
    .AddSqlAgentSecurity()
    .AddSqlAgentTelemetry(builder.Configuration);

var app = builder.Build();

// Fail at startup, not on the first request: a missing signing key, an invalid LLM setting, a semantic.yaml typo or an
// unreachable database all stop the process here with a message that says what to fix.
var settings = app.Services.GetRequiredService<ApiSettings>();
_ = settings.JwtSigningKey;
_ = app.Services.GetRequiredService<RoleAgentRegistry>();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints(settings);
app.MapChatEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> can host the API in integration tests.
public partial class Program;
