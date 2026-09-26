namespace SqlAgent.Core.Schema;

/// <summary>
/// semantic.yaml (or the database schema it is checked against) is wrong. Carries every problem found, each
/// with the YAML key path, so a typo is fixed in one round instead of one startup per error.
/// </summary>
public sealed class SemanticLayerException : Exception
{
    public SemanticLayerException(string sourcePath, IReadOnlyList<string> errors)
        : base(FormatMessage(sourcePath, errors))
    {
        SourcePath = sourcePath;
        Errors = errors;
    }

    public string SourcePath { get; }

    public IReadOnlyList<string> Errors { get; }

    private static string FormatMessage(string sourcePath, IReadOnlyList<string> errors) =>
        $"{sourcePath}: {errors.Count} problem(s) in the semantic layer:" +
        string.Concat(errors.Select(e => Environment.NewLine + " - " + e));
}
