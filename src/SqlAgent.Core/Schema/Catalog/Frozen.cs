using System.Collections.ObjectModel;

namespace SqlAgent.Core.Schema;

/// <summary>
/// Read-only copies for what the catalog hands out. A caller that casts an IReadOnlyList back to List, or an
/// IReadOnlySet to HashSet, must not be able to widen a role's allowlist for every later request.
/// </summary>
internal static class Frozen
{
    public static IReadOnlyList<T> List<T>(IEnumerable<T> items) => new ReadOnlyCollection<T>(items.ToList());

    public static IReadOnlySet<string> Set(IEnumerable<string> items) =>
        new ReadOnlySet<string>(new HashSet<string>(items, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, TValue> Map<TValue>(IEnumerable<KeyValuePair<string, TValue>> items) =>
        new ReadOnlyDictionary<string, TValue>(items.ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase));
}
