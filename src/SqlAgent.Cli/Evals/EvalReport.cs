using System.Globalization;

namespace SqlAgent.Cli.Evals;

/// <summary>Everything needed to reproduce or question a run. Written into every report.</summary>
/// <param name="Quantization">Of the local model (from Ollama); null for a hosted model.</param>
/// <param name="ProviderVersion">The Ollama server version, or the Azure OpenAI SDK version.</param>
public sealed record EvalMetadata(
    string Provider,
    string Model,
    string? Quantization,
    string? ProviderVersion,
    string GitSha,
    bool GitDirty,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    double Temperature,
    long? Seed,
    int? ContextLength,
    string ResultVisibility,
    int MaxIterations,
    int Runs);

/// <summary>A dated list price per million tokens. Cost is computed here, once, and nowhere in the running system.</summary>
public sealed record TokenPrice(decimal InputPerMillion, decimal OutputPerMillion, DateOnly AsOf);

public sealed record EvalRun(EvalMetadata Metadata, TokenPrice? Price, IReadOnlyList<ItemResult> Items);

/// <param name="Scored">Items that could be scored: those whose reference ran.</param>
public sealed record Accuracy(int Scored, int Passed)
{
    public double? Rate => Scored == 0 ? null : (double)Passed / Scored;

    public string Display => Scored == 0 ? "n/a" : $"{Passed}/{Scored} ({Percent(Rate!.Value)})";

    /// <summary>One decimal and no space before the sign, whatever the culture ("50.0%").</summary>
    public static string Percent(double rate) => (rate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>The numbers a report states, computed from the item results alone.</summary>
public sealed class EvalSummary
{
    public EvalSummary(IReadOnlyList<ItemResult> items, TokenPrice? price)
    {
        Items = items;
        Broken = items.Where(i => i.Verdict == ItemVerdict.ReferenceError).ToList();
        var scored = items.Except(Broken).ToList();

        Main = Of(scored.Where(i => !i.Holdout));
        Holdout = Of(scored.Where(i => i.Holdout));
        ByTier = GoldenSetLoader.Tiers.ToDictionary(t => t, t => Of(scored.Where(i => !i.Holdout && i.Tier == t)));
        ByPersona = scored.Where(i => !i.Holdout).GroupBy(i => i.Persona).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => Of(g));

        // The false-positive rate is about the reference queries, so it counts every item whose reference was checked.
        GuardrailFalsePositives = items.Count(i => i.GuardrailFalsePositive);
        GuardrailBlocks = scored.Sum(i => i.GuardrailBlocks);
        IterationCapHits = scored.Count(i => i.IterationCapHit);

        if (scored.Count > 0)
        {
            MeanIterations = scored.Average(i => i.Iterations);
            MeanQueries = scored.Average(i => i.QueryCount);
            MeanInputTokens = scored.Average(i => i.InputTokens);
            MeanOutputTokens = scored.Average(i => i.OutputTokens);
            var latencies = scored.Select(i => i.LatencyMs).Order().ToList();
            LatencyP50Ms = Percentile(latencies, 0.5);
            LatencyP95Ms = Percentile(latencies, 0.95);
            if (price is not null)
                CostPerQuestion = (decimal)(MeanInputTokens) * price.InputPerMillion / 1_000_000m
                                  + (decimal)(MeanOutputTokens) * price.OutputPerMillion / 1_000_000m;
        }
    }

    public IReadOnlyList<ItemResult> Items { get; }

    public IReadOnlyList<ItemResult> Broken { get; }

    /// <summary>Everything that is not a holdout item: the accuracy to quote.</summary>
    public Accuracy Main { get; }

    /// <summary>Items never used while tuning, reported on their own.</summary>
    public Accuracy Holdout { get; }

    public IReadOnlyDictionary<string, Accuracy> ByTier { get; }

    public IReadOnlyDictionary<string, Accuracy> ByPersona { get; }

    public int GuardrailFalsePositives { get; }

    public int GuardrailBlocks { get; }

    public int IterationCapHits { get; }

    public double MeanIterations { get; }

    public double MeanQueries { get; }

    public double MeanInputTokens { get; }

    public double MeanOutputTokens { get; }

    public long LatencyP50Ms { get; }

    public long LatencyP95Ms { get; }

    public decimal? CostPerQuestion { get; }

    public double GuardrailFalsePositiveRate => Items.Count == 0 ? 0 : (double)GuardrailFalsePositives / Items.Count;

    private static Accuracy Of(IEnumerable<ItemResult> items)
    {
        var list = items.ToList();
        return new Accuracy(list.Count, list.Count(i => i.Passed));
    }

    /// <summary>Nearest-rank percentile of an ascending list.</summary>
    private static long Percentile(IReadOnlyList<long> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
