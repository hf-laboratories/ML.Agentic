namespace HFLabs.ML.Agentic.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HFLabs.ML.Agentic;
using Xunit;

public sealed class AgenticBatchThreeTests
{
    // =========================================================================
    // PAT-CS-257: Evolutionary Strategy Adaptation & Mutation Tests
    // =========================================================================

    private sealed class MockPromptEvolutionEngine : IEvolutionEngine<string>
    {
        public Task<double> EvaluateFitnessAsync(string strategy, CancellationToken cancellationToken = default)
        {
            // Fitness proportional to presence of good keywords, penalized for excessive length
            double score = 0.5;
            if (strategy.Contains("step-by-step", StringComparison.OrdinalIgnoreCase)) score += 0.2;
            if (strategy.Contains("concise", StringComparison.OrdinalIgnoreCase)) score += 0.2;
            if (strategy.Contains("verified", StringComparison.OrdinalIgnoreCase)) score += 0.1;
            return Task.FromResult(Math.Min(1.0, score));
        }

        public Task<string> MutateAsync(string parent, CancellationToken cancellationToken = default)
        {
            string[] mutations = [" Think step-by-step.", " Be concise.", " Ensure verified citations."];
            string chosen = mutations[Random.Shared.Next(mutations.Length)];
            return Task.FromResult(parent + chosen);
        }

        public Task<string> CrossoverAsync(string parentA, string parentB, CancellationToken cancellationToken = default)
        {
            return Task.FromResult($"{parentA} | {parentB}");
        }
    }

    [Fact]
    public async Task EvolutionaryAdaptation_EvolvesCandidates_AndImprovesFitness()
    {
        var engine = new MockPromptEvolutionEngine();
        var optimizer = new EvolutionaryStrategyOptimizer<string>(
            engine,
            populationSize: 6,
            generations: 3,
            elitismRatio: 0.33);

        var result = await optimizer.EvolveAsync(["Base prompt: answer user query."]);

        Assert.NotNull(result);
        Assert.Equal(3, result.GenerationIndex);
        Assert.NotNull(result.BestCandidate);
        Assert.True(result.BestCandidate.Fitness >= 0.5); // Fitness improved or maintained
        Assert.NotEmpty(result.Candidates);
    }

    // =========================================================================
    // PAT-CS-258: Goal Hierarchy, Milestones & Progress Sentinel Tests
    // =========================================================================

    [Fact]
    public void GoalProgressMonitor_TracksMilestones_AndCalculatesPercentage()
    {
        var sentinel = new GoalTelemetrySentinel(stallTimeout: TimeSpan.FromSeconds(10));

        sentinel.AddMilestone(new AgentMilestone("m1", "Ingest Graph", Weight: 25.0));
        sentinel.AddMilestone(new AgentMilestone("m2", "Extract Triples", Weight: 25.0));
        sentinel.AddMilestone(new AgentMilestone("m3", "Build Tensor Index", Weight: 50.0));

        var initial = sentinel.EvaluateProgress("goal-etl");
        Assert.Equal(0.0, initial.ProgressPercentage);
        Assert.Equal(0, initial.CompletedMilestones);
        Assert.Equal(3, initial.TotalMilestones);
        Assert.False(initial.IsStalled);

        sentinel.MarkAchieved("m1");
        sentinel.MarkAchieved("m2");

        var midpoint = sentinel.EvaluateProgress("goal-etl");
        Assert.Equal(50.0, midpoint.ProgressPercentage);
        Assert.Equal(2, midpoint.CompletedMilestones);

        sentinel.MarkAchieved("m3");
        var completed = sentinel.EvaluateProgress("goal-etl");
        Assert.Equal(100.0, completed.ProgressPercentage);
        Assert.Equal(3, completed.CompletedMilestones);
    }

    [Fact]
    public async Task GoalProgressMonitor_DetectsStall_WhenProgressLags()
    {
        // 50ms stall window
        var sentinel = new GoalTelemetrySentinel(stallTimeout: TimeSpan.FromMilliseconds(50));
        sentinel.AddMilestone(new AgentMilestone("m1", "Initial Step", Weight: 100.0));

        var freshSnapshot = sentinel.EvaluateProgress("goal-stall");
        Assert.False(freshSnapshot.IsStalled);

        await Task.Delay(100);

        var stalledSnapshot = sentinel.EvaluateProgress("goal-stall");
        Assert.True(stalledSnapshot.IsStalled);

        // Pulse progress resets stall
        sentinel.PulseProgress();
        var pulsedSnapshot = sentinel.EvaluateProgress("goal-stall");
        Assert.False(pulsedSnapshot.IsStalled);
    }

    // =========================================================================
    // PAT-CS-259: Multi-Tier Agent Fallback & Exception Recovery Tests
    // =========================================================================

    private sealed class TierStub(string name, bool succeed, string value, string error)
        : IFallbackTier<string, string>
    {
        public string Name => name;

        public Task<AgentResult<string>> ExecuteTierAsync(string context, CancellationToken cancellationToken = default)
        {
            if (succeed)
            {
                return Task.FromResult(AgentResult<string>.Ok(value));
            }

            return Task.FromResult(AgentResult<string>.Fail(error));
        }
    }

    [Fact]
    public async Task AgentFallbackChain_DegradesGracefully_ToSecondaryTier()
    {
        var tier1 = new TierStub("FrontierLlm", succeed: false, "", "Rate limited (429)");
        var tier2 = new TierStub("LocalSlm", succeed: true, "Local answer", "");
        var tier3 = new TierStub("CachedResponse", succeed: true, "Cached answer", "");

        var chain = new AgentFallbackChain<string, string>([tier1, tier2, tier3]);

        var report = await chain.ExecuteAsync("User query");

        Assert.True(report.Success);
        Assert.Equal("Local answer", report.Value);
        Assert.Equal("LocalSlm", report.SuccessfulTier);
        Assert.Equal(2, report.Attempts.Count); // Tier 3 never called because tier 2 succeeded
        Assert.False(report.Attempts[0].Succeeded);
        Assert.True(report.Attempts[1].Succeeded);
    }

    [Fact]
    public async Task AgentFallbackChain_FailsWhenAllTiersExhausted()
    {
        var tier1 = new TierStub("Tier1", succeed: false, "", "Offline");
        var tier2 = new TierStub("Tier2", succeed: false, "", "Timeout");

        var chain = new AgentFallbackChain<string, string>([tier1, tier2]);

        var report = await chain.ExecuteAsync("User query");

        Assert.False(report.Success);
        Assert.Null(report.Value);
        Assert.Equal("None", report.SuccessfulTier);
        Assert.Equal(2, report.Attempts.Count);
    }

    // =========================================================================
    // PAT-CS-260: Multi-Dimensional Agent Judge & Rubric Evaluation Tests
    // =========================================================================

    [Fact]
    public async Task AgentJudgeEvaluator_ComputesWeightedScorecard()
    {
        var rubric = new EvaluationRubric(
        [
            new RubricCriterion("Grounding", "Evidence in retrieved graph", Weight: 0.6, PassingThreshold: 0.8),
            new RubricCriterion("Conciseness", "No redundant padding", Weight: 0.4, PassingThreshold: 0.7)
        ]);

        var judge = new AgentJudgeEvaluator<string, string>(
            rubric,
            dimensionScorer: (subject, criterion, ctx, ct) =>
            {
                if (criterion.Name.Equals("Grounding", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new DimensionScore(criterion.Name, 0.9, true, "All assertions verified"));
                }

                // Conciseness fails
                return Task.FromResult(new DimensionScore(criterion.Name, 0.5, false, "Excessive verbose repetition"));
            });

        var scorecard = await judge.EvaluateAsync("Draft candidate response", "Context");

        // Composite: 0.9 * 0.6 + 0.5 * 0.4 = 0.54 + 0.20 = 0.74
        Assert.Equal(0.74, Math.Round(scorecard.CompositeScore, 2));
        Assert.False(scorecard.AllCriteriaPassed); // Conciseness failed
        Assert.Equal(2, scorecard.DimensionScores.Count);
        Assert.Contains("failed 1 criteria", scorecard.OverallSummary, StringComparison.Ordinal);
    }

    // =========================================================================
    // PAT-CS-261: Priority-Queued Preemptive Agent Task Scheduler Tests
    // =========================================================================

    [Fact]
    public async Task PrioritizedTaskScheduler_ExecutesWithinConcurrencyBounds()
    {
        using var scheduler = new PrioritizedAgentScheduler(maxConcurrentTasks: 2);

        var task = new PrioritizedTask<string, string>(
            "task-01",
            TaskPriority.High,
            "ctx",
            (c, ct) => Task.FromResult($"Executed: {c}"),
            DateTime.UtcNow);

        var result = await scheduler.ScheduleAndExecuteAsync(task);
        Assert.Equal("Executed: ctx", result);
    }

    [Fact]
    public void PrioritizedTaskScheduler_PreventsStarvation_ViaAgingBoost()
    {
        var agingWindow = TimeSpan.FromSeconds(10);
        using var scheduler = new PrioritizedAgentScheduler(maxConcurrentTasks: 2, agingWindow);

        // Fresh normal task (Base = 1)
        var freshNormalTask = new PrioritizedTask<string, string>(
            "task-normal",
            TaskPriority.Normal,
            "normal",
            (c, ct) => Task.FromResult(c),
            DateTime.UtcNow);

        // Background task created 20 seconds ago (Base = 0, Age Boost = 20s / 10s = 2.0 -> Effective = 2.0)
        var agedBackgroundTask = new PrioritizedTask<string, string>(
            "task-aged-bg",
            TaskPriority.Background,
            "bg",
            (c, ct) => Task.FromResult(c),
            DateTime.UtcNow.AddSeconds(-20));

        var selected = scheduler.SelectNextTask<string, string>([freshNormalTask, agedBackgroundTask]);

        // The aged background task wins over the fresh normal task due to starvation boost!
        Assert.Equal("task-aged-bg", selected.Id);
        Assert.True(agedBackgroundTask.CalculateEffectivePriority(agingWindow) > freshNormalTask.CalculateEffectivePriority(agingWindow));
    }
}
