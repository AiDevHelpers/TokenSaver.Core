namespace AiDevTools.TokenSaver.WslControl.Tests;

using Microsoft.Extensions.Logging.Abstractions;

using AiDevTools.TokenSaver.WslControl.Core;
using AiDevTools.TokenSaver.WslControl.Models;

using Xunit;

public sealed class WslActionPlannerTests
{
    private static WslActionPlanner Build(string modelReply) =>
        new(new FakeCommandModel(modelReply), NullLogger<WslActionPlanner>.Instance);

    [Fact]
    public async Task PlanAsync_AptInstall_ParsesPackagesAndConfirms()
    {
        WslActionPlanner planner = Build(
            """{"action":"apt-install","command":"","packages":["ripgrep","fd-find"],"explanation":"install tools","needsConfirmation":true}""");

        WslActionPlan plan = await planner.PlanAsync("install ripgrep and fd");

        Assert.Equal(WslActionKind.AptInstall, plan.Kind);
        Assert.Equal(["ripgrep", "fd-find"], plan.Packages);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanAsync_StripsMarkdownFences()
    {
        WslActionPlanner planner = Build(
            "```json\n{\"action\":\"run\",\"command\":\"ls -la\",\"explanation\":\"list\",\"needsConfirmation\":false}\n```");

        WslActionPlan plan = await planner.PlanAsync("list files");

        Assert.Equal(WslActionKind.RunCommand, plan.Kind);
        Assert.Equal("ls -la", plan.Command);
    }

    [Fact]
    public async Task PlanAsync_ReadOnlyRun_DoesNotRequireConfirmation()
    {
        WslActionPlanner planner = Build(
            """{"action":"run","command":"df -h","explanation":"disk usage","needsConfirmation":false}""");

        WslActionPlan plan = await planner.PlanAsync("show disk usage");

        Assert.Equal(WslActionKind.RunCommand, plan.Kind);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanAsync_MutatingCommand_ForcesConfirmationEvenWhenModelSaysSafe()
    {
        // Model under-reports risk: a destructive rm marked needsConfirmation=false.
        WslActionPlanner planner = Build(
            """{"action":"run","command":"rm -rf /tmp/build","explanation":"clean","needsConfirmation":false}""");

        WslActionPlan plan = await planner.PlanAsync("clean the build dir");

        Assert.True(plan.RequiresConfirmation);
        Assert.NotNull(plan.Warning);
    }

    [Fact]
    public async Task PlanAsync_UnparseableReply_ReturnsUnknown()
    {
        WslActionPlanner planner = Build("I cannot help with that.");

        WslActionPlan plan = await planner.PlanAsync("do something vague");

        Assert.Equal(WslActionKind.Unknown, plan.Kind);
        Assert.Empty(plan.Command);
    }

    [Fact]
    public async Task PlanAsync_ModelThrows_DegradesToUnknown()
    {
        WslActionPlanner planner = new(
            new FakeCommandModel(new InvalidOperationException("router down")),
            NullLogger<WslActionPlanner>.Instance);

        WslActionPlan plan = await planner.PlanAsync("anything");

        Assert.Equal(WslActionKind.Unknown, plan.Kind);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public async Task PlanAsync_AptUpdate_ConfirmsByCategory()
    {
        WslActionPlanner planner = Build(
            """{"action":"apt-update","command":"apt-get update","explanation":"refresh","needsConfirmation":false}""");

        WslActionPlan plan = await planner.PlanAsync("update packages");

        Assert.Equal(WslActionKind.AptUpdate, plan.Kind);
        Assert.True(plan.RequiresConfirmation);
    }
}
