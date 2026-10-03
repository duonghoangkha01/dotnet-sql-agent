namespace SqlAgent.Core.Agent;

public enum LlmProvider
{
    Ollama,
    Azure,
}

/// <summary>
/// Which model the agent talks to and how. Switching <see cref="Provider"/> needs no code change: both providers
/// are reached through the same <c>IChatClient</c> pipeline.
/// </summary>
public sealed class LlmOptions
{
    public LlmProvider Provider { get; init; } = LlmProvider.Ollama;

    public Uri OllamaEndpoint { get; init; } = new("http://ollama:11434");

    public string OllamaModel { get; init; } = "qwen3:4b";

    /// <summary>The context window requested from Ollama. 8192 fits a GPU with about 3 GB of free VRAM.</summary>
    public int OllamaContextLength { get; init; } = 8192;

    /// <summary>No retry for the local model: a failed request fails the turn.</summary>
    public TimeSpan OllamaTimeout { get; init; } = TimeSpan.FromSeconds(180);

    public string? AzureEndpoint { get; init; }

    /// <summary>The Azure OpenAI deployment name (not the model name).</summary>
    public string? AzureDeployment { get; init; }

    public string? AzureApiKey { get; init; }

    /// <summary>The Azure SDK's own retry count. It is the only retry layer, so POSTs are never retried twice.</summary>
    public int AzureMaxRetries { get; init; } = 2;

    public TimeSpan AzureNetworkTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public float Temperature { get; init; } = 0f;

    /// <summary>Model calls one request may make, so a model that keeps calling tools cannot loop forever.</summary>
    public int MaxIterations { get; init; } = 6;

    /// <summary>Whether prompts and completions are recorded in traces. Off: they can hold query results.</summary>
    public bool CaptureContent { get; init; }

    /// <summary>The model's name for telemetry and reports: the Ollama model, or the Azure deployment.</summary>
    public string ModelName => (Provider == LlmProvider.Ollama ? OllamaModel : AzureDeployment) ?? "unknown";

    /// <summary>Sampling seed, where the provider supports one. Null leaves sampling to the provider; evals set it.</summary>
    public long? Seed { get; init; }

    /// <summary>Reads the settings from the environment names used by <c>.env</c>; unset values keep their defaults.</summary>
    /// <exception cref="InvalidOperationException">A value is malformed, or a required setting of the chosen provider is missing.</exception>
    public static LlmOptions FromEnvironment(Func<string, string?> get)
    {
        string? Value(string name) => get(name) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var defaults = new LlmOptions();
        var provider = Value("LLM_PROVIDER") switch
        {
            null => defaults.Provider,
            var p when p.Equals("ollama", StringComparison.OrdinalIgnoreCase) => LlmProvider.Ollama,
            var p when p.Equals("azure", StringComparison.OrdinalIgnoreCase) => LlmProvider.Azure,
            var p => throw new InvalidOperationException($"LLM_PROVIDER must be 'ollama' or 'azure', not '{p}'."),
        };

        var options = new LlmOptions
        {
            Provider = provider,
            OllamaEndpoint = Value("OLLAMA_ENDPOINT") is { } endpoint
                ? ParseUri("OLLAMA_ENDPOINT", endpoint)
                : defaults.OllamaEndpoint,
            OllamaModel = Value("OLLAMA_MODEL") ?? defaults.OllamaModel,
            OllamaContextLength = Value("OLLAMA_CONTEXT_LENGTH") is { } ctx
                ? ParsePositiveInt("OLLAMA_CONTEXT_LENGTH", ctx)
                : defaults.OllamaContextLength,
            AzureEndpoint = Value("AZURE_OPENAI_ENDPOINT"),
            AzureDeployment = Value("AZURE_OPENAI_DEPLOYMENT"),
            AzureApiKey = Value("AZURE_OPENAI_API_KEY"),
            Seed = Value("LLM_SEED") is { } seed
                ? long.TryParse(seed, out var parsed) ? parsed : throw new InvalidOperationException("LLM_SEED must be a whole number.")
                : null,
        };
        options.Validate();
        return options;
    }

    /// <exception cref="InvalidOperationException">A setting is out of range or missing for the chosen provider.</exception>
    public void Validate()
    {
        var errors = new List<string>();
        if (MaxIterations < 1) errors.Add("MaxIterations must be at least 1.");
        if (Temperature is < 0f or > 2f) errors.Add("Temperature must be between 0 and 2.");

        if (Provider == LlmProvider.Ollama)
        {
            if (string.IsNullOrWhiteSpace(OllamaModel)) errors.Add("OLLAMA_MODEL is empty.");
            if (OllamaContextLength < 1024) errors.Add("OLLAMA_CONTEXT_LENGTH must be at least 1024.");
            if (OllamaTimeout <= TimeSpan.Zero) errors.Add("OllamaTimeout must be positive.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(AzureEndpoint)) errors.Add("LLM_PROVIDER=azure needs AZURE_OPENAI_ENDPOINT.");
            else if (!Uri.TryCreate(AzureEndpoint, UriKind.Absolute, out _)) errors.Add("AZURE_OPENAI_ENDPOINT is not a valid URL.");
            if (string.IsNullOrWhiteSpace(AzureDeployment)) errors.Add("LLM_PROVIDER=azure needs AZURE_OPENAI_DEPLOYMENT.");
            if (string.IsNullOrWhiteSpace(AzureApiKey)) errors.Add("LLM_PROVIDER=azure needs AZURE_OPENAI_API_KEY.");
            if (AzureMaxRetries < 0) errors.Add("AzureMaxRetries cannot be negative.");
        }

        if (errors.Count > 0) throw new InvalidOperationException("Invalid LLM configuration: " + string.Join(" ", errors));
    }

    private static Uri ParseUri(string name, string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"{name} is not a valid URL.");

    private static int ParsePositiveInt(string name, string value) =>
        int.TryParse(value, out var number) && number > 0
            ? number
            : throw new InvalidOperationException($"{name} must be a positive whole number.");
}
