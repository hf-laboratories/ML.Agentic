namespace HFLabs.ML.Agentic.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HFLabs.ML.Agentic;
using Xunit;

public sealed class AgenticBatchTwoTests
{
    // =========================================================================
    // PAT-CS-251: Tool-Using Agent & Sandbox Registry Tests
    // =========================================================================

    private sealed class EchoTool : ITool
    {
        public ToolDefinition Definition => new(
            "echo",
            "Echoes back the message argument.",
            [
                new ToolParameterSchema("message", "string", "Text to echo", Required: true),
                new ToolParameterSchema("count", "int", "Repetition count", Required: false)
            ]);

        public Task<ToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            string message = arguments["message"]?.ToString() ?? string.Empty;
            int count = arguments.TryGetValue("count", out var c) && c is int cnt ? cnt : 1;
            string output = string.Concat(Enumerable.Repeat(message, count));
            return Task.FromResult(ToolExecutionResult.Ok(output));
        }
    }

    private sealed class HangingTool(TimeSpan delay) : ITool
    {
        public ToolDefinition Definition => new("hang", "Simulates long operation", []);

        public async Task<ToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return ToolExecutionResult.Ok("Completed after delay");
        }
    }

    [Fact]
    public async Task ToolRegistry_RegistersAndExecutes_WithParameterValidation()
    {
        var registry = new ToolRegistry();
        registry.Register(new EchoTool());

        Assert.True(registry.TryGetTool("echo", out var tool));
        Assert.NotNull(tool);
        Assert.Single(registry.ListTools());

        // Valid execution
        var result = await registry.ExecuteToolAsync("echo", new Dictionary<string, object>
        {
            ["message"] = "Ping!",
            ["count"] = 2
        });

        Assert.True(result.Success);
        Assert.Equal("Ping!Ping!", result.Output);
        Assert.Null(result.Error);

        // Missing required parameter "message"
        var invalidResult = await registry.ExecuteToolAsync("echo", new Dictionary<string, object>
        {
            ["count"] = 2
        });

        Assert.False(invalidResult.Success);
        Assert.NotNull(invalidResult.Error);
        Assert.Contains("Missing required parameter 'message'", invalidResult.Error, StringComparison.Ordinal);

        // Unregistered tool
        var missingResult = await registry.ExecuteToolAsync("nonexistent", new Dictionary<string, object>());
        Assert.False(missingResult.Success);
        Assert.Contains("not registered", missingResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolRegistry_EnforcesTimeout_OnSlowExecution()
    {
        // 50ms timeout, tool delays 500ms
        var registry = new ToolRegistry(defaultTimeout: TimeSpan.FromMilliseconds(50));
        registry.Register(new HangingTool(TimeSpan.FromMilliseconds(500)));

        var result = await registry.ExecuteToolAsync("hang", new Dictionary<string, object>());

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("timed out", result.Error, StringComparison.Ordinal);
    }

    // =========================================================================
    // PAT-CS-252: Plan-and-Solve Hierarchical Task Decomposition Tests
    // =========================================================================

    private sealed class MockTaskPlanner : ITaskPlanner<string>
    {
        public Task<ExecutionPlan> CreatePlanAsync(string goal, string context, CancellationToken cancellationToken = default)
        {
            var steps = new List<PlanStep>
            {
                new("step-1", "Extract graph entities", []),
                new("step-2", "Query vector index", ["step-1"]),
                new("step-3", "Synthesize findings", ["step-2"])
            };
            return Task.FromResult(new ExecutionPlan(goal, steps, IsCompleted: false));
        }

        public Task<ExecutionPlan> ReplanAsync(
            ExecutionPlan currentPlan,
            PlanStep failedStep,
            string failureReason,
            string context,
            CancellationToken cancellationToken = default)
        {
            // Replace failed step with alternate fallback step
            var newSteps = currentPlan.Steps.Select(s =>
                s.Id == failedStep.Id
                    ? new PlanStep("step-2-fallback", "Fallback to keyword index", s.DependsOn, StepStatus.Pending)
                    : s).ToList();

            // Update step-3 to depend on fallback
            newSteps = newSteps.Select(s =>
                s.Id == "step-3"
                    ? s with { DependsOn = ["step-2-fallback"] }
                    : s).ToList();

            return Task.FromResult(new ExecutionPlan(currentPlan.Goal, newSteps, IsCompleted: false, ReplansUsed: currentPlan.ReplansUsed + 1));
        }
    }

    [Fact]
    public async Task HierarchicalPlanner_ExecutesDependencySteps_AndReplansOnFailure()
    {
        var planner = new MockTaskPlanner();
        bool vectorFailed = false;

        var executor = new HierarchicalPlanExecutor<string>(
            planner,
            stepExecutor: (step, ctx, ct) =>
            {
                if (step.Id == "step-2" && !vectorFailed)
                {
                    vectorFailed = true;
                    throw new InvalidOperationException("Vector store unavailable");
                }

                return Task.FromResult($"Output for {step.Id}");
            },
            maxReplans: 2);

        var finalPlan = await executor.ExecuteGoalAsync("Multi-hop research", "GraphContext");

        Assert.True(finalPlan.IsCompleted);
        Assert.Equal(1, finalPlan.ReplansUsed);
        Assert.Contains(finalPlan.Steps, s => s.Id == "step-2-fallback" && s.Status == StepStatus.Completed);
        Assert.Contains(finalPlan.Steps, s => s.Id == "step-3" && s.Status == StepStatus.Completed);
    }

    // =========================================================================
    // PAT-CS-253: Chain-of-Verification (CoVe) Fact Grounding Pipeline Tests
    // =========================================================================

    [Fact]
    public async Task ChainOfVerification_Executes4Stages_AndGroundsDraft()
    {
        var pipeline = new ChainOfVerificationPipeline<string, string>(
            draftGenerator: (ctx, ct) => Task.FromResult("GraphRAG uses BESS on IPU and supports 1M QPS."),
            questionExtractor: (draft, ctx, ct) => Task.FromResult<IReadOnlyList<VerificationQuestion>>(
            [
                new("q1", "Does GraphRAG use BESS on IPU?", "GraphRAG uses BESS on IPU"),
                new("q2", "Does GraphRAG support 1M QPS?", "supports 1M QPS")
            ]),
            factVerifier: (q, ctx, ct) =>
            {
                if (q.Id == "q1")
                {
                    return Task.FromResult(new VerificationFactCheck(q.Id, true, "Verified in BESSReasoner.cs", 0.98));
                }

                // q2 is refuted
                return Task.FromResult(new VerificationFactCheck(q.Id, false, "Benchmark records show 12k QPS, not 1M", 0.95));
            },
            groundedSynthesizer: (draft, factChecks, ctx, ct) =>
            {
                // Correct draft based on refuted claim
                string corrected = "GraphRAG uses BESS on IPU and supports verified 12k QPS.";
                return Task.FromResult(corrected);
            });

        var result = await pipeline.VerifyAndGroundAsync("Audit performance claim");

        Assert.Equal("GraphRAG uses BESS on IPU and supports 1M QPS.", result.DraftAnswer);
        Assert.Equal("GraphRAG uses BESS on IPU and supports verified 12k QPS.", result.VerifiedAnswer);
        Assert.Equal(2, result.FactChecks.Count);
        Assert.True(result.FactChecks[0].Verified);
        Assert.False(result.FactChecks[1].Verified);
        Assert.Equal(0.5, result.OverallConfidence); // 1 out of 2 verified
    }

    // =========================================================================
    // PAT-CS-254: Multi-Persona Adversarial Debate Tests
    // =========================================================================

    private sealed class SecurityPersona : IDebatePersona
    {
        public string Name => "SecurityAuditor";
        public string Perspective => "Zero-trust and least privilege";

        public Task<DebateArgument> FormulateArgumentAsync(
            string topic,
            IReadOnlyList<DebateArgument> previousRounds,
            CancellationToken cancellationToken = default)
        {
            int round = (previousRounds.Count / 2) + 1;
            return Task.FromResult(new DebateArgument(
                Name,
                round,
                "All graph mutations must pass through cryptographic audit boundaries.",
                "Direct writes risk unauthenticated poisoned assertions.",
                Critiques: round > 1 ? ["Performance optimizer ignores blast radius."] : []));
        }
    }

    private sealed class PerformancePersona : IDebatePersona
    {
        public string Name => "PerformanceOptimizer";
        public string Perspective => "Maximum throughput and sub-millisecond latency";

        public Task<DebateArgument> FormulateArgumentAsync(
            string topic,
            IReadOnlyList<DebateArgument> previousRounds,
            CancellationToken cancellationToken = default)
        {
            int round = (previousRounds.Count / 2) + 1;
            return Task.FromResult(new DebateArgument(
                Name,
                round,
                "In-memory memory-mapped buffers must bypass serialization layers.",
                "Zero-copy SIMD and IPU tile transfers achieve 10x throughput.",
                Critiques: round > 1 ? ["Security checks cannot add synchronous lock latency."] : []));
        }
    }

    private sealed class ConsensusJudge : IDebateJudge
    {
        public Task<DebateVerdict> ArbitrateAsync(
            string topic,
            IReadOnlyList<DebateArgument> debateHistory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DebateVerdict(
                "Adopt lock-free ring-buffered ring memory with asynchronous cryptographic signing.",
                0.92,
                "Satisfies zero-copy throughput while guaranteeing tamper-evident audit trails.",
                DissentingPoints: ["Synchronous blocking check was rejected in favor of asynchronous validation."]));
        }
    }

    [Fact]
    public async Task MultiPersonaDebate_CoordinatesRounds_AndReachesConsensus()
    {
        var coordinator = new MultiPersonaDebateCoordinator(
            personas: [new SecurityPersona(), new PerformancePersona()],
            judge: new ConsensusJudge(),
            rounds: 2);

        var (verdict, transcript) = await coordinator.ConductDebateAsync("Graph Ingestion Pipeline Architecture");

        Assert.NotNull(verdict);
        Assert.Equal(4, transcript.Count); // 2 personas x 2 rounds
        Assert.Equal(0.92, verdict.Confidence);
        Assert.Contains("lock-free ring-buffered ring memory", verdict.ConsensusAnswer, StringComparison.Ordinal);
        Assert.Single(verdict.DissentingPoints);
    }

    // =========================================================================
    // PAT-CS-255: Human-in-the-Loop (HITL) Checkpoint & Approval Gate Tests
    // =========================================================================

    private sealed class MockApprovalGate(bool requireApproval, HitlVerdict reviewResult)
        : IApprovalGate<string, string>
    {
        public bool RequiresApproval(string action, string context) => requireApproval;

        public Task<CheckpointState<string>> CreateCheckpointAsync(
            string action,
            string context,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CheckpointState<string>(
                TokenId: "token-hitl-123",
                ActionName: action,
                Context: context,
                Parameters: parameters,
                Timestamp: DateTime.UtcNow,
                ExpiresAt: DateTime.UtcNow.AddMinutes(10)));
        }

        public Task<HitlVerdict> WaitForApprovalAsync(
            string tokenId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(reviewResult);
        }
    }

    [Fact]
    public async Task HumanInTheLoopGate_DirectExecution_WhenApprovalNotRequired()
    {
        var gate = new MockApprovalGate(requireApproval: false, new HitlVerdict(HitlDecision.Approved));
        var hitl = new HumanInTheLoopGate<string, string>(gate, TimeSpan.FromSeconds(5));

        var result = await hitl.ExecuteWithGateAsync(
            action: "read-query",
            context: "UserContext",
            parameters: new Dictionary<string, object> { ["query"] = "nodes" },
            executeAction: (act, p) => Task.FromResult($"Read: {p["query"]}"));

        Assert.True(result.Success);
        Assert.Equal("Read: nodes", result.Value);
    }

    [Fact]
    public async Task HumanInTheLoopGate_PausesAndResumes_OnApproval()
    {
        var gate = new MockApprovalGate(requireApproval: true, new HitlVerdict(HitlDecision.Approved, "Approved by admin"));
        var hitl = new HumanInTheLoopGate<string, string>(gate, TimeSpan.FromSeconds(5));

        var result = await hitl.ExecuteWithGateAsync(
            action: "delete-graph",
            context: "AdminContext",
            parameters: new Dictionary<string, object> { ["target"] = "legacy-partition" },
            executeAction: (act, p) => Task.FromResult($"Deleted: {p["target"]}"));

        Assert.True(result.Success);
        Assert.Equal("Deleted: legacy-partition", result.Value);
    }

    [Fact]
    public async Task HumanInTheLoopGate_AbortsExecution_OnRejection()
    {
        var gate = new MockApprovalGate(
            requireApproval: true,
            new HitlVerdict(HitlDecision.Rejected, ReviewerNotes: "Destructive action disallowed during business hours"));
        var hitl = new HumanInTheLoopGate<string, string>(gate, TimeSpan.FromSeconds(5));

        var result = await hitl.ExecuteWithGateAsync(
            action: "drop-database",
            context: "OpsContext",
            parameters: new Dictionary<string, object>(),
            executeAction: (act, p) => Task.FromResult("Dropped"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("rejected by reviewer", result.Error, StringComparison.Ordinal);
        Assert.Contains("business hours", result.Error, StringComparison.Ordinal);
    }

    // =========================================================================
    // PAT-CS-256: Agent Blackboard Shared Cognitive Workspace Tests
    // =========================================================================

    [Fact]
    public async Task AgentBlackboard_WritesAndReads_WithRevisionIncrement()
    {
        var blackboard = new AgentBlackboard();

        var entry1 = await blackboard.WriteAsync("swarm:pso:best_fitness", 0.042, "PSOAgent");
        Assert.Equal(1, entry1.Revision);
        Assert.Equal(1, blackboard.CurrentRevision);

        var entry2 = await blackboard.WriteAsync("swarm:aco:best_trail", "node_A -> node_B", "ACOAgent");
        Assert.Equal(2, entry2.Revision);
        Assert.Equal(2, blackboard.CurrentRevision);

        var read1 = await blackboard.ReadAsync<double>("swarm:pso:best_fitness");
        Assert.NotNull(read1);
        Assert.Equal(0.042, read1.Value);
        Assert.Equal("PSOAgent", read1.AuthorAgent);

        // Type mismatch returns null
        var badType = await blackboard.ReadAsync<string>("swarm:pso:best_fitness");
        Assert.Null(badType);

        var all = await blackboard.GetAllEntriesAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task AgentBlackboard_NotifiesSubscribers_OnMatchingPrefix()
    {
        var blackboard = new AgentBlackboard();
        var receivedEvents = new List<(string Key, object Value, string Author)>();

        using var sub = blackboard.Subscribe("graph:entity", (k, v, author) =>
        {
            receivedEvents.Add((k, v, author));
        });

        await blackboard.WriteAsync("graph:entity:101", "Vertex Alpha", "ExtractorA");
        await blackboard.WriteAsync("graph:relation:202", "Edge Directed", "ExtractorB");
        await blackboard.WriteAsync("graph:entity:102", "Vertex Beta", "ExtractorA");

        Assert.Equal(2, receivedEvents.Count);
        Assert.Equal("graph:entity:101", receivedEvents[0].Key);
        Assert.Equal("graph:entity:102", receivedEvents[1].Key);
    }
}
