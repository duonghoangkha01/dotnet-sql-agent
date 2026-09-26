using System.Text;
using SqlAgent.Core;
using SqlAgent.Core.Schema;

namespace SqlAgent.Cli.Commands;

/// <summary>
/// <c>emit-grants</c>: semantic.yaml to deploy/sql/40-role-grants.generated.sql. Only the offline stage of the
/// semantic layer runs (no database), so CI can regenerate the file and diff it against the committed copy.
/// </summary>
internal static class EmitGrantsCommand
{
    private const string GrantsRelativePath = "deploy/sql/40-role-grants.generated.sql";

    public static int Run(string[] args)
    {
        string? yamlPath = null;
        string? outPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yaml" when i + 1 < args.Length: yamlPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                default:
                    Console.Error.WriteLine($"emit-grants: unexpected argument '{args[i]}'.");
                    return 1;
            }
        }

        try
        {
            var config = SemanticConfigParser.Load(new AgentAssetsOptions { SemanticYamlPath = yamlPath });
            var script = GrantScriptGenerator.Generate(config);

            if (outPath == "-")
            {
                Console.Out.Write(script);
                return 0;
            }

            var target = outPath ?? DefaultOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            // UTF-8 without a BOM, LF endings: the same bytes on every machine, so the CI diff is meaningful.
            File.WriteAllText(target, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.Error.WriteLine($"Wrote {Path.GetFullPath(target)} from {config.SourcePath}");
            return 0;
        }
        catch (SemanticLayerException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"emit-grants: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"emit-grants: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The deploy/sql file of the repository that contains the current directory.</summary>
    private static string DefaultOutputPath()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SqlAgent.sln"))) return Path.Combine(dir.FullName, GrantsRelativePath);
        }

        throw new InvalidOperationException("SqlAgent.sln was not found above the current directory; pass --out <file>.");
    }
}
