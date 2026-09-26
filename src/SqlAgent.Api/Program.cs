var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Liveness only. The chat, auth and audit endpoints arrive with the agent phase.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so WebApplicationFactory<Program> can host the API in integration tests.
public partial class Program;
