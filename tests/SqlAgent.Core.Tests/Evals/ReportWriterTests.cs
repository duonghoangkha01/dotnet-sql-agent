using SqlAgent.Cli.Evals;

namespace SqlAgent.Core.Tests.Evals;

public class ReportWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sqlagent-evals-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ItemResult Result(
        string id, ItemVerdict verdict, string tier = GoldenSetLoader.SimpleTier, bool holdout = false,
        string persona = "demo-admin", bool falsePositive = false, long latency = 1000, string detail = "") =>
        new(id, persona, tier, holdout, "Question " + id, verdict, detail, Iterations: 2, QueryCount: 1, GuardrailBlocks: 0, QueryErrors: 0,
            falsePositive, IterationCapHit: false, InputTokens: 1000, OutputTokens: 100, latency, AgentSql: "SELECT 1", Answer: "x", TraceId: "t");

    private static EvalMetadata Metadata(string model = "qwen3:4b", string provider = "ollama", int day = 3) => new(
        provider, model, Quantization: "Q4_K_M", ProviderVersion: "0.12.0", GitSha: "abc123def456", GitDirty: false,
        new DateTimeOffset(2026, 10, day, 9, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, day, 10, 0, 0, TimeSpan.Zero),
        Temperature: 0, Seed: 42, ContextLength: 8192, ResultVisibility: "Summary", MaxIterations: 6, Runs: 1);

    private static EvalRun Run(EvalMetadata? metadata = null, TokenPrice? price = null) => new(metadata ?? Metadata(), price,
    [
        Result("s1", ItemVerdict.Pass),
        Result("s2", ItemVerdict.WrongResult, detail: "row count differs | expected 2, got 3", falsePositive: true),
        Result("m1", ItemVerdict.Pass, GoldenSetLoader.MultiJoinTier, persona: "demo-finance"),
        Result("t1", ItemVerdict.Fallback, GoldenSetLoader.TimeWindowTier, latency: 3000),
        Result("h1", ItemVerdict.Pass, holdout: true),
        Result("h2", ItemVerdict.WrongResult, holdout: true),
        Result("broken", ItemVerdict.ReferenceError, detail: "reference result has no rows"),
    ]);

    [Fact]
    public void Accuracy_leaves_out_holdout_and_broken_items_and_reports_the_holdout_on_its_own()
    {
        var summary = new EvalSummary(Run().Items, price: null);

        Assert.Equal("2/4 (50.0%)", summary.Main.Display);
        Assert.Equal("1/2 (50.0%)", summary.Holdout.Display);
        Assert.Equal("1/2 (50.0%)", summary.ByTier[GoldenSetLoader.SimpleTier].Display);
        Assert.Equal("1/1 (100.0%)", summary.ByTier[GoldenSetLoader.MultiJoinTier].Display);
        Assert.Equal("0/1 (0.0%)", summary.ByTier[GoldenSetLoader.TimeWindowTier].Display);
        Assert.Equal("n/a", new Accuracy(0, 0).Display);
        Assert.Single(summary.Broken);
        Assert.Equal(1, summary.GuardrailFalsePositives);
    }

    [Fact]
    public void Cost_is_computed_once_from_the_dated_price_and_only_when_given()
    {
        Assert.Null(new EvalSummary(Run().Items, price: null).CostPerQuestion);

        var price = new TokenPrice(InputPerMillion: 2m, OutputPerMillion: 8m, new DateOnly(2026, 10, 1));
        var cost = new EvalSummary(Run().Items, price).CostPerQuestion;

        // 1000 input and 100 output tokens per question: 1000 * 2/1e6 + 100 * 8/1e6.
        Assert.Equal(0.0028m, cost);
    }

    [Fact]
    public void The_markdown_states_the_method_metadata_and_every_failure()
    {
        var markdown = ReportWriter.RenderMarkdown(Run(price: new TokenPrice(2m, 8m, new DateOnly(2026, 10, 1))));

        Assert.Contains("**Single run.**", markdown);
        Assert.Contains("| Quantization | Q4_K_M |", markdown);
        Assert.Contains("abc123def456", markdown);
        Assert.Contains("| Temperature / seed | 0 / 42 |", markdown);
        Assert.Contains("Holdout (reported separately) | 1/2 (50.0%)", markdown);
        Assert.Contains("as of 2026-10-01", markdown);
        Assert.Contains("| s2 | WrongResult | row count differs \\| expected 2, got 3 |", markdown);
        Assert.Contains("could not be scored", markdown);
        Assert.DoesNotContain("Question s1", markdown); // reports hold SQL and counts, not prompts or rows
    }

    [Fact]
    public void Files_are_named_by_provider_model_and_date_and_a_second_run_never_overwrites_the_first()
    {
        var (markdown, json) = ReportWriter.WriteFiles(Run(), _dir);
        var (markdownAgain, jsonAgain) = ReportWriter.WriteFiles(Run(), _dir);

        Assert.Equal("ollama-qwen3-4b-2026-10-03.md", Path.GetFileName(markdown));
        Assert.Equal("ollama-qwen3-4b-2026-10-03.json", Path.GetFileName(json));
        Assert.Equal("ollama-qwen3-4b-2026-10-03-0930.json", Path.GetFileName(jsonAgain));
        Assert.True(File.Exists(markdownAgain));
    }

    [Fact]
    public void The_json_round_trips_and_the_latest_run_of_each_model_wins()
    {
        ReportWriter.WriteFiles(Run(Metadata(day: 1)), _dir);
        ReportWriter.WriteFiles(Run(Metadata(day: 3)), _dir);
        ReportWriter.WriteFiles(Run(Metadata("gpt-4.1", "azure", day: 2)), _dir);

        var latest = ReportWriter.LatestRuns(_dir);

        Assert.Equal(2, latest.Count);
        Assert.Equal(3, latest.Single(r => r.Metadata.Provider == "ollama").Metadata.StartedUtc.Day);
        Assert.Equal(ItemVerdict.WrongResult, latest.First().Items.First(i => i.Id == "s2").Verdict);
    }

    [Fact]
    public void The_readme_table_is_labelled_with_the_run_and_replaces_only_the_marked_block()
    {
        var readme = Path.Combine(_dir, "README.md");
        File.WriteAllText(readme, $"intro\n\n{ReportWriter.TableStartMarker}\nold table\n{ReportWriter.TableEndMarker}\n\noutro\n");

        ReportWriter.UpdateReadme(readme, ReportWriter.RenderReadmeTable([Run()]));

        var content = File.ReadAllText(readme);
        Assert.StartsWith("intro", content);
        Assert.EndsWith("outro\n", content);
        Assert.DoesNotContain("old table", content);
        Assert.Contains("Single run, qwen3:4b Q4_K_M (ollama), 2026-10-03, code abc123def456", content);
        Assert.Contains("| **All** | **2/4 (50.0%)** |", content);
    }

    [Fact]
    public void A_readme_without_the_markers_is_an_error_rather_than_a_guess()
    {
        var readme = Path.Combine(_dir, "README.md");
        File.WriteAllText(readme, "no markers here");

        Assert.Throws<InvalidOperationException>(() => ReportWriter.UpdateReadme(readme, "table"));
        Assert.Equal("no markers here", File.ReadAllText(readme));
    }

    [Fact]
    public void Without_any_result_the_table_says_nothing_has_been_published()
    {
        Assert.Contains("No eval has been published yet", ReportWriter.RenderReadmeTable([]));
    }

    [Fact]
    public void A_dot_env_file_is_read_without_comments_and_with_quotes_removed()
    {
        var values = EvalEnvironment.ParseDotEnv(["# comment", "", "A=1", "B = \"two words\"", "C='x'", "EMPTY=", "no-equals"]);

        Assert.Equal("1", values["A"]);
        Assert.Equal("two words", values["B"]);
        Assert.Equal("x", values["C"]);
        Assert.False(values.ContainsKey("EMPTY"));
        Assert.Equal(3, values.Count);
    }
}
