using System.Text.Json;
using SqlAgent.Core;
using SqlAgent.Core.Execution;

namespace SqlAgent.Cli.Evals;

/// <summary>
/// One question with the query whose result defines the right answer. <see cref="ReferenceSql"/> was checked by hand as the
/// persona's own database user, so a reference can never hold data the persona may not see. A golden item is never changed
/// to make a run pass: gaps are fixed in the prompt or semantic.yaml.
/// </summary>
/// <param name="Ordered">Whether row order is part of the answer (the question asks for a ranking and the reference has no ties).</param>
/// <param name="Holdout">Never used while tuning; reported separately so tuning cannot overfit it.</param>
public sealed record GoldenItem(
    string Id,
    string Persona,
    string Question,
    string ReferenceSql,
    string Tier,
    bool Ordered,
    bool Holdout)
{
    public UserContext User => GoldenSetLoader.UserFor(Persona);
}

public static class GoldenSetLoader
{
    public const string SimpleTier = "simple";
    public const string MultiJoinTier = "multi-join";
    public const string TimeWindowTier = "time-window";

    public static readonly IReadOnlyList<string> Tiers = [SimpleTier, MultiJoinTier, TimeWindowTier];

    /// <summary>The golden answers must fit the eval executor's row cap, so the whole result can be compared.</summary>
    public const int MaxReferenceRows = 10_000;

    /// <summary>The same fixed personas as the chat UI and the API's demo logins.</summary>
    private static readonly IReadOnlyDictionary<string, (Role Role, int? Territory)> Personas = new Dictionary<string, (Role, int?)>
    {
        ["demo-sales-rep-nw"] = (Role.SalesRep, 1),
        ["demo-finance"] = (Role.Finance, null),
        ["demo-admin"] = (Role.Admin, null),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static UserContext UserFor(string persona) =>
        Personas.TryGetValue(persona, out var p) ? new UserContext(p.Role, p.Territory) : throw new ArgumentException($"Unknown persona '{persona}'.");

    /// <exception cref="InvalidOperationException">The file is unreadable or any item is invalid; the message lists every problem.</exception>
    public static IReadOnlyList<GoldenItem> LoadFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Cannot read {path}: {ex.Message}", ex);
        }
    }

    public static IReadOnlyList<GoldenItem> Parse(string json, string sourceName = "golden set")
    {
        List<GoldenItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<GoldenItem>>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{sourceName} is not valid: {ex.Message}", ex);
        }

        if (items is null or []) throw new InvalidOperationException($"{sourceName} has no items.");

        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var where = $"item {i + 1} ({item?.Id ?? "no id"})";
            if (item is null) { errors.Add($"item {i + 1} is empty"); continue; }
            if (string.IsNullOrWhiteSpace(item.Id)) errors.Add($"{where}: id is required");
            else if (!ids.Add(item.Id)) errors.Add($"{where}: duplicate id");
            if (!Personas.ContainsKey(item.Persona ?? "")) errors.Add($"{where}: persona must be one of {string.Join(", ", Personas.Keys)}");
            if (string.IsNullOrWhiteSpace(item.Question)) errors.Add($"{where}: question is required");
            if (string.IsNullOrWhiteSpace(item.ReferenceSql)) errors.Add($"{where}: referenceSql is required");
            if (!Tiers.Contains(item.Tier ?? "")) errors.Add($"{where}: tier must be one of {string.Join(", ", Tiers)}");
        }

        if (errors.Count > 0) throw new InvalidOperationException($"{sourceName} is not valid:\n  " + string.Join("\n  ", errors));
        return items;
    }
}
