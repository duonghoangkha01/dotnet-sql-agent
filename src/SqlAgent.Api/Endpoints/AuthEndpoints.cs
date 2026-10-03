using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SqlAgent.Core;
using SqlAgent.Core.Execution;

namespace SqlAgent.Api.Endpoints;

/// <summary>One of the fixed demo identities. The <c>sub</c> is fixed, so a persona always owns the same conversations.</summary>
public sealed record DemoPersona(string Id, Role Role, int? TerritoryId, string DisplayName)
{
    public static IReadOnlyList<DemoPersona> All { get; } =
    [
        new("demo-sales-rep-nw", Role.SalesRep, 1, "Sales rep, Northwest"),
        new("demo-finance", Role.Finance, null, "Finance"),
        new("demo-admin", Role.Admin, null, "Admin"),
    ];

    public static DemoPersona? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

public sealed record DemoTokenRequest(string? Persona);

public static class AuthEndpoints
{
    public const string RoleClaim = "role";
    public const string TerritoryClaim = "territory";
    public const string DemoTokenRateLimitPolicy = "demo-token";

    /// <summary>
    /// <c>POST /api/auth/demo-token</c>: a signed token for one of the fixed personas. Mapped only when demo auth is on;
    /// otherwise the route does not exist at all (404).
    /// </summary>
    public static void MapAuthEndpoints(this IEndpointRouteBuilder routes, ApiSettings settings)
    {
        if (!settings.DemoAuth) return;

        routes.MapPost("/api/auth/demo-token", (DemoTokenRequest request) =>
        {
            var persona = DemoPersona.Find(request.Persona);
            if (persona is null)
            {
                return Results.BadRequest(new { error = "Unknown persona. Use one of: " + string.Join(", ", DemoPersona.All.Select(p => p.Id)) });
            }

            var lifetime = settings.TokenLifetime;
            return Results.Ok(new
            {
                token = Issue(persona, settings.JwtSigningKey, lifetime),
                expiresInSeconds = (int)lifetime.TotalSeconds,
                persona = new { id = persona.Id, role = persona.Role.ToKey(), territoryId = persona.TerritoryId, displayName = persona.DisplayName },
            });
        }).RequireRateLimiting(DemoTokenRateLimitPolicy);
    }

    public static string Issue(DemoPersona persona, string signingKey, TimeSpan lifetime, DateTime? issuedAtUtc = null)
    {
        var issuedAt = issuedAtUtc ?? DateTime.UtcNow;
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, persona.Id), new(RoleClaim, persona.Role.ToKey()) };
        if (persona.TerritoryId is { } territory) claims.Add(new Claim(TerritoryClaim, territory.ToString()));

        var credentials = new SigningCredentials(SigningKey(signingKey), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(ApiSettings.Issuer, ApiSettings.Audience, claims,
            notBefore: issuedAt, expires: issuedAt.Add(lifetime), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static SymmetricSecurityKey SigningKey(string key) => new(Encoding.UTF8.GetBytes(key));

    /// <summary>The caller's identity from the validated token, or null if the claims do not describe a usable user.</summary>
    public static (string Sub, UserContext User)? ReadUser(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(sub) || !RoleExtensions.TryParse(principal.FindFirstValue(RoleClaim), out var role)) return null;

        int? territory = int.TryParse(principal.FindFirstValue(TerritoryClaim), out var parsed) ? parsed : null;
        if (role == Role.SalesRep && territory is null) return null;
        return (sub, new UserContext(role, territory));
    }
}
