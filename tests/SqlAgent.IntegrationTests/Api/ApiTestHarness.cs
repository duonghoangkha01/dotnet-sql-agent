using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlAgent.Api;
using SqlAgent.Api.Endpoints;
using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Schema;
using SqlAgent.Core.Tests.Agent.Fakes;
using Microsoft.Extensions.AI;

namespace SqlAgent.IntegrationTests.Api;

/// <summary>
/// The real API (authentication, limits, endpoints, the agent loop and the tools) hosted in memory, with only the outer
/// edges replaced: the model is scripted, the catalog is a stub, and queries and the audit log are fakes. No LLM and no
/// database are needed.
/// </summary>
public sealed class ApiTestHarness : WebApplicationFactory<Program>
{
    public const string SigningKey = "test-signing-key-for-integration-tests-0123456789";

    public ApiTestHarness(ScriptedChatClient model, Dictionary<string, string?>? settings = null, FakeQueryExecutor? executor = null, SseOptions? sse = null)
    {
        Model = model;
        Executor = executor ?? new FakeQueryExecutor();
        Settings = settings ?? [];
        Sse = sse;
    }

    public ScriptedChatClient Model { get; }

    public FakeQueryExecutor Executor { get; }

    public RecordingAuditSink Audit { get; } = new();

    private Dictionary<string, string?> Settings { get; }

    private SseOptions? Sse { get; }

    public ConversationStore Store => Services.GetRequiredService<ConversationStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            var values = new Dictionary<string, string?>
            {
                ["JWT_SIGNING_KEY"] = SigningKey,
                ["DEMO_AUTH"] = "true",
                // Generous by default: tests that exercise a limit lower it.
                ["CHAT_RATE_LIMIT_PER_MINUTE"] = "1000",
                ["DEMO_TOKEN_RATE_LIMIT_PER_MINUTE"] = "1000",
            };
            foreach (var (key, value) in Settings) values[key] = value;
            config.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(sp => ChatClientFactory.Wrap(Model, sp.GetRequiredService<LlmOptions>()));
            services.RemoveAll<ISchemaCatalog>();
            services.AddSingleton<ISchemaCatalog>(new StubSchemaCatalog());
            services.RemoveAll<IQueryExecutor>();
            services.AddSingleton<IQueryExecutor>(Executor);
            services.RemoveAll<IAuditSink>();
            services.AddSingleton<IAuditSink>(Audit);
            if (Sse is not null)
            {
                services.RemoveAll<SseOptions>();
                services.AddSingleton(Sse);
            }
        });
    }

    /// <summary>A client carrying a demo token for the persona, obtained through the real token endpoint.</summary>
    public async Task<HttpClient> ClientForAsync(string persona)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/demo-token", new { persona });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static Task<HttpResponseMessage> Chat(HttpClient client, string message, string? conversationId = null, CancellationToken ct = default) =>
        client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream") { Content = JsonContent.Create(new { conversationId, message }) },
            HttpCompletionOption.ResponseHeadersRead,
            ct);
}

public sealed record SseMessage(string Event, JsonElement Data)
{
    public string String(string property) => Data.GetProperty(property).GetString()!;
}

/// <summary>Reads a server-sent event stream one event at a time.</summary>
public sealed class SseReader(HttpResponseMessage response) : IAsyncDisposable
{
    private readonly Task<Stream> _stream = response.Content.ReadAsStreamAsync();
    private StreamReader? _reader;

    public int Pings { get; private set; }

    /// <summary>The next event, or null when the stream has ended.</summary>
    public async Task<SseMessage?> NextAsync(CancellationToken ct = default)
    {
        _reader ??= new StreamReader(await _stream);
        string? name = null;
        string? data = null;
        while (await _reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith(':'))
            {
                Pings++;
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
            else if (line.Length == 0 && name is not null)
            {
                return new SseMessage(name, JsonDocument.Parse(data ?? "{}").RootElement.Clone());
            }
        }

        return null;
    }

    public async Task<List<SseMessage>> ReadAllAsync(CancellationToken ct = default)
    {
        var all = new List<SseMessage>();
        while (await NextAsync(ct) is { } message) all.Add(message);
        return all;
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        response.Dispose();
        return ValueTask.CompletedTask;
    }
}
