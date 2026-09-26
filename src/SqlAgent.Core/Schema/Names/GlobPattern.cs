using System.Text;
using System.Text.RegularExpressions;

namespace SqlAgent.Core.Schema;

/// <summary>
/// A name pattern where '*' matches any run of characters and '?' matches one. Case-insensitive.
/// The same semantics are emitted as T-SQL LIKE by <see cref="GrantScriptGenerator"/>, so the C# allowlist
/// and the generated database grants expand a pattern to the same columns.
/// </summary>
public sealed class GlobPattern
{
    private readonly Regex _regex;

    public GlobPattern(string pattern)
    {
        Pattern = pattern;
        _regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public string Pattern { get; }

    public bool IsWildcard => HasWildcard(Pattern);

    public bool IsMatch(string name) => _regex.IsMatch(name);

    public static bool HasWildcard(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>The pattern as a T-SQL LIKE operand (to be used with <c>ESCAPE N'\'</c>).</summary>
    public static string ToLikePattern(string pattern)
    {
        var like = new StringBuilder();
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*': like.Append('%'); break;
                case '?': like.Append('_'); break;
                case '%' or '_' or '[' or '\\': like.Append('\\').Append(ch); break;
                default: like.Append(ch); break;
            }
        }

        return like.ToString();
    }
}
