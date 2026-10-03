using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlAgent.Cli.Evals;

/// <summary>
/// Writes <c>evals/results/&lt;provider&gt;-&lt;model&gt;-&lt;date&gt;.{md,json}</c> and the README table generated from the latest
/// result of each provider and model. The JSON is the record; the Markdown and the table are views of it.
/// </summary>
public static class ReportWriter
{
    public const string TableStartMarker = "<!-- eval-results:start -->";
    public const string TableEndMarker = "<!-- eval-results:end -->";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes both files and returns their paths. A second run on the same day gets a time suffix instead of overwriting the first.</summary>
    public static (string Markdown, string JsonFile) WriteFiles(EvalRun run, string directory)
    {
        Directory.CreateDirectory(directory);
        var baseName = $"{Slug(run.Metadata.Provider)}-{Slug(run.Metadata.Model)}-{run.Metadata.StartedUtc:yyyy-MM-dd}";
        if (File.Exists(Path.Combine(directory, baseName + ".json"))) baseName += $"-{run.Metadata.StartedUtc:HHmm}";

        var markdown = Path.Combine(directory, baseName + ".md");
        var jsonFile = Path.Combine(directory, baseName + ".json");
        File.WriteAllText(markdown, RenderMarkdown(run), new UTF8Encoding(false));
        File.WriteAllText(jsonFile, JsonSerializer.Serialize(run, Json), new UTF8Encoding(false));
        return (markdown, jsonFile);
    }

    public static string Slug(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9._]+", "-").Trim('-');

    public static string RenderMarkdown(EvalRun run)
    {
        var meta = run.Metadata;
        var summary = new EvalSummary(run.Items, run.Price);
        var text = new StringBuilder();

        text.AppendLine($"# Eval: {meta.Model} ({meta.Provider}), {meta.StartedUtc:yyyy-MM-dd}")
            .AppendLine()
            .AppendLine($"**Single run.** Execution accuracy on {summary.Main.Scored} golden questions: **{summary.Main.Display}**.")
            .AppendLine("A run is one pass at temperature 0 with a fixed seed where the provider honors one: it can differ on another run (see evals/README.md).")
            .AppendLine();

        text.AppendLine("## Run")
            .AppendLine()
            .AppendLine("| | |").AppendLine("|---|---|")
            .AppendLine($"| Provider / model | {meta.Provider} / {meta.Model} |")
            .AppendLine($"| Quantization | {meta.Quantization ?? "n/a"} |")
            .AppendLine($"| Provider version | {meta.ProviderVersion ?? "unknown"} |")
            .AppendLine($"| Code | {meta.GitSha}{(meta.GitDirty ? " + uncommitted changes" : "")} |")
            .AppendLine($"| Temperature / seed | {meta.Temperature.ToString(CultureInfo.InvariantCulture)} / {(meta.Seed?.ToString(CultureInfo.InvariantCulture) ?? "none")} |")
            .AppendLine($"| Context window | {(meta.ContextLength?.ToString(CultureInfo.InvariantCulture) ?? "provider default")} |")
            .AppendLine($"| Result visibility | {meta.ResultVisibility} |")
            .AppendLine($"| Model-call cap per question | {meta.MaxIterations} |")
            .AppendLine($"| Started / finished (UTC) | {meta.StartedUtc:yyyy-MM-dd HH:mm} / {meta.FinishedUtc:yyyy-MM-dd HH:mm} |")
            .AppendLine();

        text.AppendLine("## Accuracy")
            .AppendLine()
            .AppendLine("| Set | Passed |").AppendLine("|---|---|")
            .AppendLine($"| All (without holdout) | {summary.Main.Display} |");
        foreach (var (tier, accuracy) in summary.ByTier) text.AppendLine($"| {tier} | {accuracy.Display} |");
        foreach (var (persona, accuracy) in summary.ByPersona) text.AppendLine($"| persona {persona} | {accuracy.Display} |");
        text.AppendLine($"| Holdout (reported separately) | {summary.Holdout.Display} |").AppendLine();

        text.AppendLine("## Behavior")
            .AppendLine()
            .AppendLine("| | |").AppendLine("|---|---|")
            .AppendLine($"| Guardrail false positives (reference queries refused) | {summary.GuardrailFalsePositives}/{summary.Items.Count} ({Accuracy.Percent(summary.GuardrailFalsePositiveRate)}), target under 5% |")
            .AppendLine($"| Guardrail refusals of the agent's own queries | {summary.GuardrailBlocks} |")
            .AppendLine($"| Iteration-cap hits | {summary.IterationCapHits} |")
            .AppendLine($"| Model calls per question (mean) | {summary.MeanIterations.ToString("0.0", CultureInfo.InvariantCulture)} |")
            .AppendLine($"| run_sql calls per question (mean) | {summary.MeanQueries.ToString("0.0", CultureInfo.InvariantCulture)} |")
            .AppendLine($"| Tokens per question (mean, in / out) | {summary.MeanInputTokens.ToString("0", CultureInfo.InvariantCulture)} / {summary.MeanOutputTokens.ToString("0", CultureInfo.InvariantCulture)} |")
            .AppendLine($"| Latency p50 / p95 | {summary.LatencyP50Ms / 1000.0:0.0} s / {summary.LatencyP95Ms / 1000.0:0.0} s |")
            .AppendLine($"| Cost per question | {CostText(summary, run.Price)} |")
            .AppendLine();

        var failures = run.Items.Where(i => !i.Passed && i.Verdict != ItemVerdict.ReferenceError).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine("## Failures").AppendLine()
                .AppendLine("| Id | Result | Why |").AppendLine("|---|---|---|");
            foreach (var item in failures) text.AppendLine($"| {item.Id} | {item.Verdict} | {Cell(item.Detail)} |");
            text.AppendLine();
        }

        if (summary.Broken.Count > 0)
        {
            text.AppendLine("## Golden items that could not be scored").AppendLine()
                .AppendLine("Left out of every figure above. A broken reference is fixed in the golden set, never by changing what counts as right.")
                .AppendLine().AppendLine("| Id | Why |").AppendLine("|---|---|");
            foreach (var item in summary.Broken) text.AppendLine($"| {item.Id} | {Cell(item.Detail)} |");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The table for the repository README: one block per provider and model, from its latest result.</summary>
    public static string RenderReadmeTable(IReadOnlyList<EvalRun> latestRuns)
    {
        if (latestRuns.Count == 0) return "No eval has been published yet. Run `SqlAgent.Cli eval` (see [evals/README.md](evals/README.md)).";

        var text = new StringBuilder();
        foreach (var run in latestRuns.OrderBy(r => r.Metadata.Provider).ThenBy(r => r.Metadata.Model))
        {
            var meta = run.Metadata;
            var summary = new EvalSummary(run.Items, run.Price);
            if (text.Length > 0) text.AppendLine();
            text.AppendLine($"Single run, {meta.Model}{(meta.Quantization is null ? "" : " " + meta.Quantization)} ({meta.Provider}), {meta.StartedUtc:yyyy-MM-dd}, code {meta.GitSha}:")
                .AppendLine()
                .AppendLine("| Questions | Passed |").AppendLine("|---|---|");
            foreach (var (tier, accuracy) in summary.ByTier) text.AppendLine($"| {tier} | {accuracy.Display} |");
            text.AppendLine($"| **All** | **{summary.Main.Display}** |");
            if (summary.Holdout.Scored > 0) text.AppendLine($"| Holdout (reported separately) | {summary.Holdout.Display} |");
            text.AppendLine().AppendLine(
                $"Guardrail false positives {Accuracy.Percent(summary.GuardrailFalsePositiveRate)}, " +
                $"{summary.MeanIterations.ToString("0.0", CultureInfo.InvariantCulture)} model calls and p50 latency {summary.LatencyP50Ms / 1000.0:0.0} s per question. Cost: {CostText(summary, run.Price)}.");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Replaces what is between the markers. Fails, rather than guessing a place, when the README has none.</summary>
    public static void UpdateReadme(string readmePath, string table)
    {
        var content = File.ReadAllText(readmePath);
        var start = content.IndexOf(TableStartMarker, StringComparison.Ordinal);
        var end = content.IndexOf(TableEndMarker, StringComparison.Ordinal);
        if (start < 0 || end < start)
            throw new InvalidOperationException($"{readmePath} needs the markers {TableStartMarker} and {TableEndMarker} where the table goes.");

        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var block = TableStartMarker + newline + table.Replace("\r\n", "\n").Replace("\n", newline) + newline + TableEndMarker;
        File.WriteAllText(readmePath, content[..start] + block + content[(end + TableEndMarker.Length)..], new UTF8Encoding(false));
    }

    /// <summary>The newest result of each provider and model in the results directory.</summary>
    public static IReadOnlyList<EvalRun> LatestRuns(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => JsonSerializer.Deserialize<EvalRun>(File.ReadAllText(path), Json))
            .OfType<EvalRun>()
            .GroupBy(r => (r.Metadata.Provider, r.Metadata.Model))
            .Select(g => g.MaxBy(r => r.Metadata.StartedUtc)!)
            .ToList();
    }

    private static string CostText(EvalSummary summary, TokenPrice? price) => summary.CostPerQuestion is { } cost && price is not null
        ? $"${cost.ToString("0.0000", CultureInfo.InvariantCulture)} (list price ${price.InputPerMillion}/${price.OutputPerMillion} per million input/output tokens, as of {price.AsOf:yyyy-MM-dd})"
        : "not computed (no price given; a local model has none)";

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
