using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace SqlAgent.Core.Agent;

/// <summary>
/// Assembles the one <c>IChatClient</c> pipeline every role agent shares:
/// function invocation (capped) → OpenTelemetry → provider. OpenTelemetry sits directly above the provider and
/// appears once, so each model call is one span and nothing is traced twice. There is no resilience handler: Azure
/// retries inside its SDK and the local model is not retried, so a POST is never retried by two layers.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>The <c>ActivitySource</c> name of the chat-client spans; add it to the tracer provider.</summary>
    public const string TelemetrySourceName = "SqlAgent.Llm";

    public static IChatClient Create(LlmOptions options, ILoggerFactory? loggerFactory = null)
    {
        options.Validate();
        return Wrap(CreateProviderClient(options), options, loggerFactory);
    }

    /// <summary>Adds the shared pipeline around any client. Tests pass a scripted fake here and still get real tool invocation.</summary>
    public static IChatClient Wrap(IChatClient inner, LlmOptions options, ILoggerFactory? loggerFactory = null)
    {
        var pipeline = new ChatClientBuilder(inner);

        // The first Use is the outermost layer.
        pipeline.UseFunctionInvocation(loggerFactory, client =>
        {
            client.MaximumIterationsPerRequest = options.MaxIterations;
            client.AllowConcurrentInvocation = false;
            client.IncludeDetailedErrors = false;
        });
        pipeline.UseOpenTelemetry(loggerFactory, TelemetrySourceName, client => client.EnableSensitiveData = options.CaptureContent);

        if (options.Provider == LlmProvider.Ollama)
        {
            // Ollama's context size is a per-request option, and its default (often 2k-4k) would silently
            // truncate the prompt. Set it for every call, whoever creates the ChatOptions.
            var contextLength = options.OllamaContextLength;
            pipeline.ConfigureOptions(chatOptions =>
            {
                if (chatOptions.RawRepresentationFactory is not null) return;
                chatOptions.RawRepresentationFactory = _ => new ChatRequest
                {
                    Options = new OllamaSharp.Models.RequestOptions { NumCtx = contextLength },
                };
            });
        }

        return pipeline.Build();
    }

    private static IChatClient CreateProviderClient(LlmOptions options) =>
        options.Provider == LlmProvider.Ollama ? CreateOllama(options) : CreateAzure(options);

    private static IChatClient CreateOllama(LlmOptions options)
    {
        var http = new HttpClient { BaseAddress = options.OllamaEndpoint, Timeout = options.OllamaTimeout };
        return new OllamaApiClient(http, options.OllamaModel);
    }

    private static IChatClient CreateAzure(LlmOptions options)
    {
        var clientOptions = new AzureOpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(options.AzureMaxRetries),
            NetworkTimeout = options.AzureNetworkTimeout,
        };
        var client = new AzureOpenAIClient(new Uri(options.AzureEndpoint!), new ApiKeyCredential(options.AzureApiKey!), clientOptions);
        return client.GetChatClient(options.AzureDeployment!).AsIChatClient();
    }
}
