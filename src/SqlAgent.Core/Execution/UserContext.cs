namespace SqlAgent.Core.Execution;

/// <summary>
/// Who a query runs for: the role picks the database user, and a sales rep's territory scopes the rows they see.
/// </summary>
public sealed record UserContext
{
    public UserContext(Role role, int? territoryId = null)
    {
        // A rep with no territory would see no rows at all (row-level security compares against NULL): fail loudly instead.
        if (role == Role.SalesRep && territoryId is null)
            throw new ArgumentException("A sales rep needs a territory.", nameof(territoryId));

        Role = role;
        TerritoryId = territoryId;
    }

    public Role Role { get; }

    /// <summary>Only used for <see cref="Role.SalesRep"/>; finance and admin see every territory.</summary>
    public int? TerritoryId { get; }
}
