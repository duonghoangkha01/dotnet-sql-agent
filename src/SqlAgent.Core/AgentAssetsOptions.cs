using System.Reflection;

namespace SqlAgent.Core;

/// <summary>
/// Where the shared agent assets come from. By default they are the copies embedded in this assembly, so the
/// Api and the Cli (evals, emit-grants) always read the same files. A path overrides one asset for local editing.
/// </summary>
public sealed class AgentAssetsOptions
{
    public const string SemanticYamlResource = "assets/semantic.yaml";

    /// <summary>Shown in error messages when the embedded copy is used.</summary>
    public const string EmbeddedSemanticYamlPath = "SqlAgent.Core/assets/semantic.yaml";

    /// <summary>Reads semantic.yaml from this file instead of the embedded copy.</summary>
    public string? SemanticYamlPath { get; init; }

    /// <summary>The YAML text and the path to report in errors.</summary>
    public (string Path, string Content) ReadSemanticYaml()
    {
        if (SemanticYamlPath is not null)
        {
            return (Path.GetFullPath(SemanticYamlPath), File.ReadAllText(SemanticYamlPath));
        }

        using var stream = typeof(AgentAssetsOptions).Assembly.GetManifestResourceStream(SemanticYamlResource)
            ?? throw new InvalidOperationException($"Embedded resource '{SemanticYamlResource}' is missing from {Assembly.GetExecutingAssembly().GetName().Name}.");
        using var reader = new StreamReader(stream);
        return (EmbeddedSemanticYamlPath, reader.ReadToEnd());
    }
}
