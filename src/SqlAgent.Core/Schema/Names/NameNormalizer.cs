using System.Text;

namespace SqlAgent.Core.Schema;

/// <summary>
/// One spelling for object names, shared by the catalog and the guardrail: brackets and double quotes
/// are stripped, parts are trimmed, and parts are joined with '.'. Comparison is case-insensitive
/// (every set and dictionary that holds normalized names uses <see cref="StringComparer.OrdinalIgnoreCase"/>).
/// </summary>
public static class NameNormalizer
{
    public static string Table(string schema, string name) => Normalize(schema) + "." + Normalize(name);

    /// <summary>Normalizes a possibly qualified name such as <c>[Sales].[SalesOrderHeader]</c> or <c>"sales"."x"</c>.</summary>
    public static string Normalize(string name) => string.Join('.', SplitParts(name));

    /// <summary>Splits on dots outside [brackets] and "quotes", then strips the quoting.</summary>
    public static IReadOnlyList<string> SplitParts(string name)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? closer = null;

        foreach (var ch in name)
        {
            if (closer is not null)
            {
                if (ch == closer) closer = null;
                else current.Append(ch);
            }
            else if (ch == '[') closer = ']';
            else if (ch == '"') closer = '"';
            else if (ch == '.')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(ch);
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }
}
