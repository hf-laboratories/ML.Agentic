namespace HFLabs.ML.Agentic.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HFLabs.ML.Agentic;
using Xunit;

public sealed class AgenticBatchFourTests
{
    // =========================================================================
    // PAT-CS-262: Tree-of-Thoughts / MCTS Graph Exploration Tests
    // =========================================================================

    private sealed class MockMctsProblem : IMctsProblem<string, string>
    {
        public Task<IReadOnlyList<(string Action, string NextState)>> GenerateCandidateActionsAsync(
            string state,
            CancellationToken cancellationToken = default)
        {
            if (state == "root")
            {
                IReadOnlyList<(string, string)> transitions =
                [
                    ("choose_suboptimal_path", "branch_a"),
                    ("choose_optimal_path", "branch_b")
                ];
                return Task.FromResult(transitions);
            }

            if (state == "branch_a")
            {
                IReadOnlyList<(string, string)> transitions =
                [
                    ("step_a1", "leaf_a1")
                ];
                return Task.FromResult(transitions);
            }

            if (state == "branch_b")
            {
                IReadOnlyList<(string, string)> transitions =
                [
                    ("step_b1", "leaf_b1")
                ];
                return Task.FromResult(transitions);
            }

            return Task.FromResult<IReadOnlyList<(string, string)>>([]);
        }

        public Task<double> EvaluateTerminalStateAsync(
            string state,
            CancellationToken cancellationToken = default)
        {
            // branch_b yields 0.9 reward; branch_a yields 0.1 reward
            double reward = state switch
            {
                "leaf_b1" or "branch_b" => 0.9,
                "leaf_a1" or "branch_a" => 0.1,
                _ => 0.2
            };

            return Task.FromResult(reward);
        }

        public bool IsTerminal(string state) => state.StartsWith("leaf_", StringComparison.Ordinal);
    }

    [Fact]
    public async Task MctsExploration_ExploresTree_AndSelectsOptimalPath()
    {
        var problem = new MockMctsProblem();
        var engine = new MctsExplorationEngine<string, string>(problem, explorationConstant: 1.414);

        var bestNode = await engine.ExploreAsync("root", iterations: 30);

        Assert.NotNull(bestNode);
        Assert.Equal("choose_optimal_path", bestNode.ActionFromParent);
        Assert.Equal("branch_b", bestNode.State);
        Assert.True(bestNode.AverageReward >= 0.8);
        Assert.True(bestNode.VisitCount > 0);
    }

    // =========================================================================
    // PAT-CS-263: Resource-Aware Compute & Budget Ledger Tests
    // =========================================================================

    [Fact]
    public void ResourceBudgetLedger_TracksTokensAndCost_AndEnforcesThreshold()
    {
        var ledger = new ResourceBudgetLedger(
            maxTokens: 10_000,
            maxCostUsd: 0.50,
            maxToolCalls: 5,
            maxLatency: TimeSpan.FromMinutes(1));

        Assert.True(ledger.HasBudgetRemaining());
        Assert.False(ledger.IsWarningWatermarkExceeded(0.75));

        // Consume 8,000 tokens (80% utilization)
        ledger.RecordConsumption(tokens: 8_000, costUsd: 0.10, toolCalls: 1);

        Assert.True(ledger.HasBudgetRemaining());
        Assert.True(ledger.IsWarningWatermarkExceeded(0.75));

        var snapshot = ledger.GetSnapshot();
        Assert.Equal(8_000, snapshot.TokensUsed);
        Assert.Equal(0.8, snapshot.TokenUtilization);
        Assert.Equal(1, snapshot.ToolCallsMade);

        // Exhaust token ceiling
        ledger.RecordConsumption(tokens: 2_500);
        Assert.False(ledger.HasBudgetRemaining());
    }

    [Fact]
    public void ResourceBudgetLedger_DetectsDiminishingReturns_AndSignalsEarlyStop()
    {
        var ledger = new ResourceBudgetLedger();

        // High marginal gain: no early stop
        Assert.False(ledger.CheckDiminishingReturns(marginalGain: 0.15, epsilon: 0.01, patience: 2));

        // First low gain iteration: count = 1, patience = 2 -> false
        Assert.False(ledger.CheckDiminishingReturns(marginalGain: 0.005, epsilon: 0.01, patience: 2));

        // Second low gain iteration: count = 2 >= patience -> true
        Assert.True(ledger.CheckDiminishingReturns(marginalGain: 0.002, epsilon: 0.01, patience: 2));

        // Reset check with high gain
        Assert.False(ledger.CheckDiminishingReturns(marginalGain: 0.05, epsilon: 0.01, patience: 2));
    }

    // =========================================================================
    // PAT-CS-264: Program-Aided Reasoning (PAL) Code Agent Tests
    // =========================================================================

    private sealed class MockExpressionExecutor(Func<string, ProgramExecutionResult> executionFunc) : IExpressionExecutor
    {
        public Task<ProgramExecutionResult> ExecuteExpressionAsync(
            string codeExpression,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(executionFunc(codeExpression));
        }
    }

    [Fact]
    public async Task ProgramAidedReasoning_ExecutesExpression_AndReturnsExactResult()
    {
        var executor = new MockExpressionExecutor(code =>
        {
            if (code.Contains("6 * 7", StringComparison.Ordinal))
            {
                return ProgramExecutionResult.Ok(42, TimeSpan.FromMilliseconds(5));
            }
            return ProgramExecutionResult.Fail("Unknown expression");
        });

        var agent = new ProgramAidedReasoningAgent<string, string>(
            name: "MathPalAgent",
            codeGenerator: (ctx, ct) => Task.FromResult("int result = 6 * 7;"),
            executor: executor,
            resultProjector: (ctx, output) => $"The computed answer is {output}.");

        var result = await agent.ExecuteAsync("What is 6 times 7?");

        Assert.True(result.Success);
        Assert.Equal("The computed answer is 42.", result.Value);
        Assert.Contains("MathPalAgent", result.Trace[0], StringComparison.Ordinal);
        Assert.Contains("Program evaluated successfully", result.Trace[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramAidedReasoning_HandlesExecutionFailure_Gracefully()
    {
        var executor = new MockExpressionExecutor(_ =>
            ProgramExecutionResult.Fail("Compilation error: Division by zero"));

        var agent = new ProgramAidedReasoningAgent<string, string>(
            name: "FaultyPalAgent",
            codeGenerator: (ctx, ct) => Task.FromResult("int result = 1 / 0;"),
            executor: executor,
            resultProjector: (ctx, output) => output.ToString() ?? "");

        var result = await agent.ExecuteAsync("Compute 1/0");

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Contains("Division by zero", result.Error, StringComparison.Ordinal);
    }

    // =========================================================================
    // PAT-CS-265: Rolling Context Compactor & Semantic Anchor Tests
    // =========================================================================

    private sealed class MockContextSummarizer : IContextSummarizer
    {
        public Task<string> SummarizeTurnsAsync(
            string existingSummary,
            IReadOnlyList<ConversationTurn> turnsToCompact,
            CancellationToken cancellationToken = default)
        {
            string newEntries = string.Join("; ", turnsToCompact.Select(t => $"{t.Role}: {t.Content}"));
            string combined = string.IsNullOrEmpty(existingSummary)
                ? newEntries
                : $"{existingSummary} | {newEntries}";
            return Task.FromResult(combined);
        }
    }

    [Fact]
    public async Task RollingContextCompactor_CompactsOldTurns_AndPreservesAnchors()
    {
        var summarizer = new MockContextSummarizer();
        var compactor = new RollingContextCompactor(summarizer, maxActiveTurns: 3);

        // Turn 1: Pinned Anchor (e.g., initial user constraints or graph schema)
        var anchor = new ConversationTurn("System", "User constraint: All responses must cite graph nodes.", DateTime.UtcNow, IsAnchor: true);
        await compactor.AddTurnAsync(anchor);

        // Add 3 regular turns (Total: 1 anchor + 3 unpinned = 4 items, unpinned <= 3)
        await compactor.AddTurnAsync(new ConversationTurn("User", "Turn 1 query", DateTime.UtcNow));
        await compactor.AddTurnAsync(new ConversationTurn("Assistant", "Turn 1 answer", DateTime.UtcNow));
        await compactor.AddTurnAsync(new ConversationTurn("User", "Turn 2 query", DateTime.UtcNow));

        var preCompactionSnapshot = compactor.GetCompactedSnapshot();
        Assert.Equal(4, preCompactionSnapshot.ActiveTurns.Count);
        Assert.Equal(string.Empty, preCompactionSnapshot.ConsolidatedSummary);

        // Add 4th unpinned turn -> unpinned count becomes 4 > maxActiveTurns(3) -> triggers eviction of Turn 1 query
        await compactor.AddTurnAsync(new ConversationTurn("Assistant", "Turn 2 answer", DateTime.UtcNow));

        var postCompactionSnapshot = compactor.GetCompactedSnapshot();

        // Anchor must STILL be present in active turns
        Assert.Contains(postCompactionSnapshot.ActiveTurns, t => t.IsAnchor);
        Assert.Equal(4, postCompactionSnapshot.ActiveTurns.Count); // 1 anchor + 3 active unpinned turns
        Assert.Contains("Turn 1 query", postCompactionSnapshot.ConsolidatedSummary, StringComparison.Ordinal);
        Assert.Equal(5, postCompactionSnapshot.OriginalTurnCount);
        Assert.True(postCompactionSnapshot.TokensSavedEstimate > 0);
    }
}
