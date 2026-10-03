using SqlAgent.Core.Agent;

namespace SqlAgent.Core.Tests.Agent;

public class LlmOptionsDeepSeekTests
{
    private static LlmOptions Load(params (string Name, string Value)[] env)
    {
        var map = env.ToDictionary(e => e.Name, e => e.Value);
        return LlmOptions.FromEnvironment(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void DeepSeek_reads_key_and_defaults_to_the_chat_model()
    {
        var options = Load(("LLM_PROVIDER", "deepseek"), ("DEEPSEEK_API_KEY", "sk-test"));

        Assert.Equal(LlmProvider.DeepSeek, options.Provider);
        Assert.Equal("deepseek-chat", options.ModelName);
        Assert.Equal(new Uri("https://api.deepseek.com"), options.DeepSeekEndpoint);
    }

    [Fact]
    public void DeepSeek_without_a_key_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("LLM_PROVIDER", "deepseek")));

        Assert.Contains("DEEPSEEK_API_KEY", error.Message);
    }

    [Fact]
    public void DeepSeek_model_and_endpoint_can_be_overridden()
    {
        var options = Load(("LLM_PROVIDER", "DeepSeek"), ("DEEPSEEK_API_KEY", "k"),
            ("DEEPSEEK_MODEL", "deepseek-v4"), ("DEEPSEEK_ENDPOINT", "https://example.test/v1"));

        Assert.Equal("deepseek-v4", options.ModelName);
        Assert.Equal(new Uri("https://example.test/v1"), options.DeepSeekEndpoint);
    }

    [Fact]
    public void An_unknown_provider_names_all_three_choices()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("LLM_PROVIDER", "nope")));

        Assert.Contains("deepseek", error.Message);
    }
}
