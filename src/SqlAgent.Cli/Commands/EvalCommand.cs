using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlAgent.Cli.Evals;
using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;
using SqlAgent.Core.Governance;
using SqlAgent.Core.Guardrails;
using SqlAgent.Core.Schema;

namespace SqlAgent.Cli.Commands;

/// <summary>
/// <c>eval</c>: runs the golden set through the real agent (the Api's prompt, tools, semantic layer and executor, built from
/// Core) and writes the report. It needs the model and the database, so it runs by hand and never in CI.
/// </summary>
internal static class EvalCommand
{
    public const string Usage = """
          eval [--golden <file>] [--out <dir>] [--only <id,id,...>] [--limit <n>] [--seed <n>]
               [--price-in <usd/1M>] [--price-out <usd/1M>] [--price-date <yyyy-MM-dd>]
               [--update-readme] [--readme-only]
              Runs evals/golden.json through the agent and writes evals/results/<provider>-<model>-<date>.{md,json}.
              Settings come from the environment or .env (LLM_PROVIDER, OLLAMA_*, AZURE_OPENAI_*, SQL_*_PASSWORD) and
              default to a stack opened with compose.dev.yml. The seed defaults to 42. --update-readme rewrites the table
              between the eval-results markers of README.md from the latest results; --readme-only does only that.
              --price-in/--price-out (with --price-date) add a cost per question from a published list price.
        """;

    private sealed record Options(
        string? Golden, string? Out, HashSet<string>? Only, int? Limit, string? Seed,
        decimal? PriceIn, decimal? PriceOut, DateOnly? PriceDate, bool UpdateReadme, bool ReadmeOnly);

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"eval: {ex.Message}");
            return 1;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            var env = new EvalEnvironment(EvalEnvironment.FindRepositoryRoot());
            return await ExecuteAsync(options, env, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("eval: cancelled; nothing was written.");
            return 130;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SemanticLayerException or IOException)
        {
            Console.Error.WriteLine($"eval: {ex.Message}");
            return 1;
        }
        catch (SqlException ex)
        {
            Console.Error.WriteLine($"eval: the database could not be reached or refused a login: {ex.Errors[0].Message} " +
                                    "Is the stack up with compose.dev.yml, and are the SQL_* settings right?");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(Options options, EvalEnvironment env, CancellationToken ct)
    {
        var root = env.RepositoryRoot ?? throw new InvalidOperationException("SqlAgent.sln was not found above the current directory; run from inside the repository.");
        var resultsDir = options.Out ?? Path.Combine(root, "evals", "results");
        var readme = Path.Combine(root, "README.md");

        if (options.ReadmeOnly)
        {
            ReportWriter.UpdateReadme(readme, ReportWriter.RenderReadmeTable(ReportWriter.LatestRuns(resultsDir)));
            Console.Error.WriteLine($"Updated the eval table in {readme}");
            return 0;
        }

        var items = SelectItems(GoldenSetLoader.LoadFile(options.Golden ?? Path.Combine(root, "evals", "golden.json")), options);
        if (options.Seed is { } seed) env.Override("LLM_SEED", seed);
        var llm = env.Llm();
        var price = options.PriceIn is { } input && options.PriceOut is { } output
            ? new TokenPrice(input, output, options.PriceDate ?? throw new InvalidOperationException("--price-in and --price-out need --price-date: a price without its date proves nothing."))
            : null;

        var limits = new QueryLimits(MaxRows: GoldenSetLoader.MaxReferenceRows, MaxResultBytes: 256L * 1024 * 1024);
        var guardrail = new SqlGuardrail(limits);
        var executor = new SafeQueryExecutor(env.RoleConnections(), limits);
        var assets = new AgentAssetsOptions();
        var catalog = await SemanticLayerLoader.LoadAsync(assets, env.AppConnectionString(), ct);

        using var loggerFactory = new StderrLoggerFactory();
        var chat = ChatClientFactory.Create(llm, loggerFactory);
        var registry = RoleAgentRegistry.Create(chat, catalog, assets, llm, loggerFactory);
        var agentOptions = AgentOptions.For(llm, env.ResultVisibility());
        var turns = new AgentTurnRunner(registry, catalog, guardrail, executor, NullAuditSink.Instance, agentOptions, loggerFactory.CreateLogger<AgentTurnRunner>());

        var (quantization, providerVersion) = await EvalEnvironment.DescribeProviderAsync(llm, ct);
        var (sha, dirty) = env.GitState();
        var started = DateTimeOffset.UtcNow;
        Console.Error.WriteLine($"Running {items.Count} questions on {llm.Provider}/{llm.ModelName} (seed {llm.Seed?.ToString() ?? "none"}).");

        var results = new List<ItemResult>();
        using (var runner = new EvalRunner(turns, catalog, guardrail, executor, llm.MaxIterations))
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var result = await runner.RunAsync(item, ct);
                results.Add(result);
                Console.Error.WriteLine(
                    $"[{results.Count}/{items.Count}] {item.Id,-18} {result.Verdict,-14} {result.LatencyMs / 1000.0,5:0.0}s " +
                    $"{result.Iterations} calls{(result.Detail.Length > 0 ? " - " + result.Detail : "")}");
            }
        }

        var metadata = new EvalMetadata(
            llm.Provider.ToString().ToLowerInvariant(), llm.ModelName, quantization, providerVersion, sha, dirty,
            started, DateTimeOffset.UtcNow, llm.Temperature, llm.Seed,
            llm.Provider == LlmProvider.Ollama ? llm.OllamaContextLength : null,
            agentOptions.ResultVisibility.ToString(), llm.MaxIterations, Runs: 1);
        var run = new EvalRun(metadata, price, results);

        var (markdown, json) = ReportWriter.WriteFiles(run, resultsDir);
        Console.Error.WriteLine($"Wrote {markdown}{Environment.NewLine}Wrote {json}");

        if (options.UpdateReadme)
        {
            ReportWriter.UpdateReadme(readme, ReportWriter.RenderReadmeTable(ReportWriter.LatestRuns(resultsDir)));
            Console.Error.WriteLine($"Updated the eval table in {readme}");
        }

        var summary = new EvalSummary(results, price);
        Console.WriteLine($"Execution accuracy: {summary.Main.Display}; holdout: {summary.Holdout.Display}");
        // A broken golden item means the numbers cover fewer questions than intended: say so in the exit code too.
        return summary.Broken.Count > 0 ? 2 : 0;
    }

    private static IReadOnlyList<GoldenItem> SelectItems(IReadOnlyList<GoldenItem> all, Options options)
    {
        IEnumerable<GoldenItem> selected = all;
        if (options.Only is { } only)
        {
            var unknown = only.Except(all.Select(i => i.Id), StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0) throw new InvalidOperationException($"--only names unknown items: {string.Join(", ", unknown)}.");
            selected = selected.Where(i => only.Contains(i.Id));
        }

        if (options.Limit is { } limit) selected = selected.Take(limit);
        return selected.ToList();
    }

    private static Options Parse(string[] args)
    {
        string? golden = null, outDir = null, seed = null;
        HashSet<string>? only = null;
        int? limit = null;
        decimal? priceIn = null, priceOut = null;
        DateOnly? priceDate = null;
        bool updateReadme = false, readmeOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--golden": golden = Next(); break;
                case "--out": outDir = Next(); break;
                case "--only": only = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase); break;
                case "--limit": limit = int.TryParse(Next(), out var n) && n > 0 ? n : throw new ArgumentException("--limit must be a positive whole number."); break;
                case "--seed": seed = Next(); break;
                case "--price-in": priceIn = Money(Next(), "--price-in"); break;
                case "--price-out": priceOut = Money(Next(), "--price-out"); break;
                case "--price-date": priceDate = DateOnly.TryParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new ArgumentException("--price-date must be yyyy-MM-dd."); break;
                case "--update-readme": updateReadme = true; break;
                case "--readme-only": readmeOnly = true; break;
                default: throw new ArgumentException($"unexpected argument '{args[i]}'.");
            }
        }

        if ((priceIn is null) != (priceOut is null)) throw new ArgumentException("--price-in and --price-out go together.");
        return new Options(golden, outDir, only, limit, seed, priceIn, priceOut, priceDate, updateReadme, readmeOnly);
    }

    private static decimal Money(string text, string name) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : throw new ArgumentException($"{name} must be a non-negative number.");
}
