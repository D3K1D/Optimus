using OptimizationTool.Engine.Agent;
using Xunit;

namespace OptimizationTool.Tests;

/// <summary>The answer's last "Next: …" line becomes follow-up questions (m5-chat §7).</summary>
public sealed class FollowUpTests
{
    [Fact]
    public void Next_line_becomes_up_to_three_followups_and_leaves_the_text()
    {
        var (text, asks) = AgentService.SplitFollowUps("Your firewall is on.\n\nNext: Which app uses port 49697? | Should I close it? | Is Steam safe? | A fourth one");
        Assert.Equal("Your firewall is on.", text);
        Assert.Equal(["Which app uses port 49697?", "Should I close it?", "Is Steam safe?"], asks);
    }

    [Theory]
    [InlineData("**Next:** Check startup apps? | ok")]
    [InlineData("next: Check startup apps? | ok")]
    public void Bold_or_lowercase_next_lines_work_and_short_items_drop(string line)
    {
        var (text, asks) = AgentService.SplitFollowUps("Done.\n" + line);
        Assert.Equal("Done.", text);
        Assert.Equal(["Check startup apps?"], asks);
    }

    [Fact]
    public void Without_a_next_line_nothing_changes_and_citations_are_stripped_from_questions()
    {
        var (plain, none) = AgentService.SplitFollowUps("Plain answer.");
        Assert.Equal("Plain answer.", plain);
        Assert.Empty(none);
        var (_, asks) = AgentService.SplitFollowUps("A\nNext: What is [ev-0123456789abcdef] about?");
        Assert.Equal(["What is about?"], asks);
    }
}
