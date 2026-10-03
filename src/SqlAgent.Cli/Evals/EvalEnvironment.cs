using System.Diagnostics;
using Microsoft.Data.SqlClient;
using OllamaSharp;
using SqlAgent.Core;
using SqlAgent.Core.Agent;
using SqlAgent.Core.Execution;

namespace SqlAgent.Cli.Evals;

/// <summary>
/// Where an eval run finds its settings: the process environment first, then the repository's <c>.env</c> (the file
/// the compose stack reads), then defaults for a stack opened to the host with <c>compose.dev.yml</c> (SQL Server on
/// 127.0.0.1:1433, Ollama on 127.0.0.1:11434).
/// </summary>
internal sealed class EvalEnvironment
{
    private readonly Dictionary<string, string> _dotEnv;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public EvalEnvironment(string? repositoryRoot)
    {
        RepositoryRoot = repositoryRoot;
        _dotEnv = repositoryRoot is not null && File.Exists(Path.Combine(repositoryRoot, ".env"))
            ? ParseDotEnv(File.ReadAllLines(Path.Combine(repositoryRoot, ".env")))
            : [];
    }

    public string? RepositoryRoot { get; }

    /// <summary>Takes precedence over the environment, for a command-line option.</summary>
    public void Override(string name, string value) => _overrides[name] = value;

    public string? Get(string name) =>
        _overrides.GetValueOrDefault(name)
        ?? (Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromProcess ? fromProcess : null)
        ?? _dotEnv.GetValueOrDefault(name);

    private string Required(string name) => Get(name) ?? throw new InvalidOperationException($"{name} is not set (in the environment or in .env).");

    public LlmOptions Llm() => LlmOptions.FromEnvironment(name => name switch
    {
        // Evals are repeatable runs: a fixed seed unless the caller says otherwise.
        "LLM_SEED" => Get(name) ?? "42",
        "OLLAMA_ENDPOINT" => Get(name) ?? "http://127.0.0.1:11434",
        _ => Get(name),
    });

    public ResultVisibility ResultVisibility() => Get("RESULT_VISIBILITY") is { } text
        ? Enum.TryParse<ResultVisibility>(text, ignoreCase: true, out var visibility)
            ? visibility
            : throw new InvalidOperationException("RESULT_VISIBILITY must be None, Summary or Rows.")
        : Core.Agent.ResultVisibility.Summary;

    private string SqlServer => Get("SQL_SERVER") ?? "127.0.0.1";

    private string Database => Get("SQL_DATABASE") ?? "AdventureWorks2022";

    private bool TrustServerCertificate => !(Get("SQL_TRUST_SERVER_CERTIFICATE") is { } v && (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0"));

    public RoleConnectionFactory RoleConnections() => new(
        SqlServer,
        Database,
        new Dictionary<Role, string>
        {
            [Role.SalesRep] = Required("SQL_SALES_REP_PASSWORD"),
            [Role.Finance] = Required("SQL_FINANCE_PASSWORD"),
            [Role.Admin] = Required("SQL_ADMIN_PASSWORD"),
        },
        TrustServerCertificate);

    /// <summary>The <c>sqlagent_app</c> connection: schema metadata only, never business data.</summary>
    public string AppConnectionString() => new SqlConnectionStringBuilder
    {
        DataSource = SqlServer,
        InitialCatalog = Database,
        UserID = "sqlagent_app",
        Password = Required("SQL_APP_PASSWORD"),
        TrustServerCertificate = TrustServerCertificate,
        ApplicationName = "sqlagent-eval",
        PersistSecurityInfo = false,
    }.ConnectionString;

    /// <summary>The model's quantization and the server's version, for the report. Best effort: a missing detail is reported as unknown.</summary>
    public static async Task<(string? Quantization, string? Version)> DescribeProviderAsync(LlmOptions llm, CancellationToken ct)
    {
        if (llm.Provider == LlmProvider.Azure)
            return (null, "Azure.AI.OpenAI " + typeof(Azure.AI.OpenAI.AzureOpenAIClient).Assembly.GetName().Version);
        if (llm.Provider == LlmProvider.DeepSeek)
            return (null, "OpenAI " + typeof(OpenAI.OpenAIClient).Assembly.GetName().Version + " via " + llm.DeepSeekEndpoint.Host);

        try
        {
            using var http = new HttpClient { BaseAddress = llm.OllamaEndpoint, Timeout = TimeSpan.FromSeconds(10) };
            var client = new OllamaApiClient(http, llm.OllamaModel);
            var version = await client.GetVersionAsync(ct);
            var model = await client.ShowModelAsync(llm.OllamaModel, ct);
            return (model.Details?.QuantizationLevel, version.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>The commit the run used, and whether the working tree had changes on top of it.</summary>
    public (string Sha, bool Dirty) GitState()
    {
        var sha = Git("rev-parse --short=12 HEAD");
        if (sha is null) return ("unknown", false);
        return (sha, !string.IsNullOrWhiteSpace(Git("status --porcelain")));
    }

    private string? Git(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = RepositoryRoot ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The directory above the current one that holds <c>SqlAgent.sln</c>, or null outside a checkout.</summary>
    public static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SqlAgent.sln"))) return dir.FullName;
        }

        return null;
    }

    /// <summary>KEY=VALUE lines; blank lines and # comments are skipped, surrounding quotes removed.</summary>
    public static Dictionary<string, string> ParseDotEnv(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            if (value.Length > 0) values[line[..eq].Trim()] = value;
        }

        return values;
    }
}
