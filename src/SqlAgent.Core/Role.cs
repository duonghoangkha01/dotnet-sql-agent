namespace SqlAgent.Core;

/// <summary>The three agent roles. Each has its own DB user, allowlist and role agent.</summary>
public enum Role
{
    SalesRep,
    Finance,
    Admin,
}

public static class RoleExtensions
{
    public static IReadOnlyList<Role> All { get; } = [Role.SalesRep, Role.Finance, Role.Admin];

    /// <summary>The key used in semantic.yaml, in tokens and in DB user names.</summary>
    public static string ToKey(this Role role) => role switch
    {
        Role.SalesRep => "sales_rep",
        Role.Finance => "finance",
        Role.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>The database user the role's queries run as (see deploy/sql/10-logins-and-users.sql).</summary>
    public static string ToDbUser(this Role role) => "sqlagent_" + role.ToKey();

    public static bool TryParse(string? key, out Role role)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToKey(), key, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }
}
