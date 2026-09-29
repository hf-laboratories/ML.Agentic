namespace HFLabs.ML.Agentic.Tests;

using HFLabs.ML.Agentic.Escalation;
using Xunit;

public sealed class EscalationPrimitiveTests
{
    [Fact]
    public void Budget_AllowsConfiguredAttemptsPerKey_AndResets()
    {
        var budget = new PerKeyEscalationBudget(maxAttemptsPerKey: 2);

        budget.RecordAttempt("t1");
        Assert.True(budget.CanEscalate("t1"));
        budget.RecordAttempt("t1");
        Assert.False(budget.CanEscalate("t1"));
        Assert.True(budget.CanEscalate("t2"));

        budget.Reset("t1");
        Assert.True(budget.CanEscalate("t1"));
        Assert.False(new PerKeyEscalationBudget(-3).CanEscalate("any"));
    }

    [Fact]
    public async Task Run_Exhausted_BuildsAndSendsNothing()
    {
        var budget = new PerKeyEscalationBudget(0);
        bool touched = false;

        var run = await BoundedEscalation.RunAsync<string, string, bool>(
            "t1",
            budget,
            _ => { touched = true; return Task.FromResult("req"); },
            (_, _) => { touched = true; return Task.FromResult<string?>("p"); },
            (_, _, _) => { touched = true; return Task.FromResult(true); });

        Assert.Equal(BoundedEscalationStatus.BudgetExhausted, run.Status);
        Assert.False(touched);
    }

    [Fact]
    public async Task Run_ProposerUnavailable_StillConsumesTheAttempt_AndSkipsValidation()
    {
        var budget = new PerKeyEscalationBudget(1);
        bool validated = false;

        var run = await BoundedEscalation.RunAsync<string, string, bool>(
            "t1",
            budget,
            _ => Task.FromResult("req"),
            (_, _) => Task.FromResult<string?>(null),
            (_, _, _) => { validated = true; return Task.FromResult(true); });

        Assert.Equal(BoundedEscalationStatus.ProposerUnavailable, run.Status);
        Assert.Equal("req", run.Request);
        Assert.False(validated);
        Assert.False(budget.CanEscalate("t1"));
    }

    [Fact]
    public async Task Run_PassesRequestAndProposalToValidator()
    {
        var run = await BoundedEscalation.RunAsync<string, string, string>(
            "t1",
            new PerKeyEscalationBudget(1),
            _ => Task.FromResult("req"),
            (request, _) => Task.FromResult<string?>(request + ":proposal"),
            (request, proposal, _) => Task.FromResult($"{request}|{proposal}"));

        Assert.Equal(BoundedEscalationStatus.Validated, run.Status);
        Assert.Equal("req:proposal", run.Proposal);
        Assert.Equal("req|req:proposal", run.Verdict);
    }
}
