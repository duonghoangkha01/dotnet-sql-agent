using System.Collections.Concurrent;
using System.Diagnostics;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Tests.Agent.Fakes;
using static SqlAgent.Core.Tests.Agent.Fakes.ScriptedChatClient;
using static SqlAgent.IntegrationTests.Api.ApiTestHarness;

namespace SqlAgent.IntegrationTests.Api;

public class TelemetryTests
{
    [Fact]
    public async Task One_turn_is_one_trace_with_one_span_per_model_call_and_no_prompt_or_result_text()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ChatClientFactory.TelemetrySourceName or AgentTelemetry.SourceName or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using var api = new ApiTestHarness(new ScriptedChatClient(
            RunSql("SELECT SalesOrderID FROM Sales.SalesOrderHeader"), Reply("There are 3 orders.")));
        using var client = await api.ClientForAsync("demo-finance");
        using var response = await Chat(client, "How many orders?");
        await using var reader = new SseReader(response);
        var events = await reader.ReadAllAsync();

        var traceId = events.Single(e => e.Event == "trace").String("traceId");
        var inTrace = spans.Where(s => s.TraceId.ToString() == traceId).ToList();

        // The request span, and exactly one span for each of the two model calls (so telemetry is not applied twice).
        Assert.Contains(inTrace, s => s.Source.Name == "Microsoft.AspNetCore");
        var llmSpans = inTrace.Where(s => s.Source.Name == ChatClientFactory.TelemetrySourceName).ToList();
        Assert.True(llmSpans.Count(s => s.OperationName.StartsWith("chat")) == 2,
            "expected one chat span per model call, saw: " + string.Join(", ", llmSpans.Select(s => s.DisplayName)));
        Assert.Equal(traceId, Assert.Single(api.Audit.Entries).TraceId);

        // The guardrail's verdict is part of the same trace, as a child of the request, with the verdict and nothing of the SQL.
        var guardrail = Assert.Single(inTrace, s => s.OperationName == "guardrail.validate");
        Assert.Equal(true, guardrail.GetTagItem("allowed"));
        Assert.NotEqual(default, guardrail.ParentSpanId);
        // The tool calls the model made run inside the model client's loop and are traced in this trace too.
        Assert.Contains(inTrace, s => s.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));

        // Prompts and completions can contain query results: they are not captured unless asked for.
        var recorded = inTrace.SelectMany(s => s.TagObjects).Select(t => t.Value?.ToString() ?? "");
        Assert.DoesNotContain(recorded, text => text.Contains("name1") || text.Contains("How many orders?") || text.Contains("There are 3 orders."));
    }
}
