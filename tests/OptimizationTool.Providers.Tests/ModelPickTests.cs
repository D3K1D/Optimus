namespace OptimizationTool.Providers.Tests;

/// <summary>The model a check picks from what the account lists (never a default the account cannot use).</summary>
public sealed class ModelPickTests
{
    [Fact]
    public void Anthropic_keeps_the_default_when_listed_else_prefers_opus_then_sonnet()
    {
        Assert.Equal("claude-opus-5-5", AnthropicAdapter.PickModel(["claude-haiku-5-5", "claude-opus-5-5"], "claude-opus-5-5"));
        Assert.Equal("claude-opus-5-5", AnthropicAdapter.PickModel([], "claude-opus-5-5"));
        Assert.Equal("claude-opus-5-1", AnthropicAdapter.PickModel(["claude-sonnet-5-5", "claude-opus-5-1"], "claude-opus-5-5"));
        Assert.Equal("claude-sonnet-5-5", AnthropicAdapter.PickModel(["claude-haiku-5-5", "claude-sonnet-5-5"], "claude-opus-5-5"));
        Assert.Equal("claude-haiku-5-5", AnthropicAdapter.PickModel(["claude-instant-1", "claude-haiku-5-5"], "claude-opus-5-5"));
        Assert.Equal("claude-opus-5-5", AnthropicAdapter.PickModel(["something-else"], "claude-opus-5-5"));
    }

    [Fact]
    public void OpenAi_keeps_the_default_when_listed_else_the_highest_full_size_reasoning_model()
    {
        Assert.Equal("gpt-6.1-sol", OpenAiAdapter.PickModel(["gpt-6-astra", "gpt-6.1-sol"], "gpt-6.1-sol"));
        Assert.Equal("gpt-6-astra", OpenAiAdapter.PickModel(["gpt-5-mini", "gpt-6-astra", "gpt-6-mini", "o4-mini"], "gpt-6.1-sol"));
        Assert.Equal("o4-mini", OpenAiAdapter.PickModel(["o4-mini"], "gpt-6.1-sol"));
        Assert.Equal("gpt-6.1-sol", OpenAiAdapter.PickModel(["gpt-4o"], "gpt-6.1-sol"));
    }

    [Fact]
    public void ChatGpt_plan_keeps_the_default_when_listed_else_the_plans_first_model()
    {
        Assert.Equal("gpt-6.1-sol", ChatGptPlanAdapter.PickModel(["gpt-6-terra", "gpt-6.1-sol"], "gpt-6.1-sol"));
        Assert.Equal("gpt-6-terra", ChatGptPlanAdapter.PickModel(["gpt-6-terra", "gpt-6-luna"], "gpt-6.1-sol"));
        Assert.Equal("gpt-6.1-sol", ChatGptPlanAdapter.PickModel([], "gpt-6.1-sol"));
    }
}
