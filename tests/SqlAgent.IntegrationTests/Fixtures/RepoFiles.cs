using System.Text.RegularExpressions;

namespace SqlAgent.IntegrationTests.Fixtures;

/// <summary>
/// Finds files of the repository from a test run, and reads the pinned SQL Server image and the AdventureWorks
/// backup (URL and checksum) from the files the local stack itself uses, so the tests run on exactly those.
/// </summary>
internal static partial class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string DeploySqlDirectory => Path.Combine(Root, "deploy", "sql");

    /// <summary>The numbered scripts (<c>NN-*.sql</c>) of deploy/sql in the order apply-scripts.sh runs them.</summary>
    public static IEnumerable<string> NumberedSqlScripts() =>
        Directory.GetFiles(DeploySqlDirectory, "*.sql")
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^\d\d-"))
            .Order(StringComparer.Ordinal);

    /// <summary>The <c>image:</c> of the sqlserver service in docker-compose.yml, digest included.</summary>
    public static string SqlServerImage()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));
        var match = SqlServerImagePattern().Match(compose);
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("No SQL Server image found in docker-compose.yml.");
    }

    /// <summary>The download URL and SHA-256 that deploy/fetch-bak.sh verifies.</summary>
    public static (string Url, string Sha256) AdventureWorksBackup()
    {
        var script = File.ReadAllText(Path.Combine(Root, "deploy", "fetch-bak.sh"));
        var url = Regex.Match(script, "^URL=\"([^\"]+)\"", RegexOptions.Multiline);
        var sha = Regex.Match(script, "^SHA256=\"([0-9a-f]{64})\"", RegexOptions.Multiline);
        return url.Success && sha.Success
            ? (url.Groups[1].Value, sha.Groups[1].Value)
            : throw new InvalidOperationException("Could not read URL and SHA256 from deploy/fetch-bak.sh.");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SqlAgent.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("SqlAgent.sln was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^\s*image:\s*(mcr\.microsoft\.com/mssql/server\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SqlServerImagePattern();
}
