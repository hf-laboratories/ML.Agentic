# HFLabs.ML.Agentic

[![NuGet](https://img.shields.io/badge/nuget-v1.0.0-blue.svg)](https://nuget.org)
[![Framework](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com)
[![Dependencies](https://img.shields.io/badge/dependencies-0%20External%20Packages-brightgreen.svg)](https://github.com)
[![Tests](https://img.shields.io/badge/tests-62%20Passing%20(218ms)-brightgreen.svg)](https://github.com)
[![License](https://img.shields.io/badge/license-Apache--2.0-green.svg)](https://github.com)

**`HFLabs.ML.Agentic`** provides zero-dependency, in-memory building blocks for enterprise multi-agent systems, protocol serialization (MCP, A2A, JSON-RPC 2.0), and cognitive orchestration on modern .NET 10.

No Python, no graph store requirements, and **0 external `PackageReference` or `ProjectReference` dependencies**. Every primitive runs directly on core .NET standard libraries with sub-millisecond deterministic execution.

---

## 🧩 The 21 Core Agentic Primitives

### 1. Coordination & Governance
* **`AgentBlackboard` (`IAgentBlackboard`)** — Shared, typed knowledge atom repository with granular topic subscriptions.
* **`HumanInTheLoopGate` (`IApprovalGate`)** — Deterministic checkpointing for human review (approve, reject, modify).
* **`ResourceBudgetLedger`** — Strict token, cost, and tool-call accounting with watermark alerts and diminishing-returns detection.
* **`AgentGuardrailPipeline` (`IInputGuardrail`, `IOutputGuardrail`)** — Ordered bidirectional policy and safety gates.
* **`PrioritizedAgentScheduler`** — Priority-queued agent task scheduler with starvation-prevention aging.
* **`GoalTelemetrySentinel`** — Objective milestone tracking and drift monitoring.

### 2. Planning & Cognitive Reasoning
* **`HierarchicalPlanExecutor` (`ITaskPlanner`)** — Decomposes complex goals into multi-stage execution trees.
* **`MctsExplorationEngine` (`IMctsProblem`)** — Monte Carlo Tree Search for combinatorial decision-making.
* **`ReflectionLoop`** — Self-critique, evaluation, and recursive candidate refinement.
* **`MultiPersonaDebateCoordinator`** — Multi-agent adversarial debate with independent judge synthesis.
* **`ChainOfVerificationPipeline`** — Fact verification against grounding evidence with claim-level validation.
* **`ProgramAidedReasoningAgent`** — Deterministic code/expression execution for exact mathematical reasoning.
* **`EvolutionaryStrategyOptimizer`** — Genetic and fitness-based prompt/strategy evolution.

### 3. Routing, Topologies & Fault Tolerance
* **`AgentEventBus<TEvent>`** — Resilient in-process pub/sub where subscriber faults never compromise the bus.
* **`DependencyLayerExecutor<TTask, TOutcome>`** — Concurrent DAG executor running topological layers in parallel with cycle detection.
* **`AgentFailoverRetry`** — Exponential backoff retry routing around degraded nodes in a pool.
* **`BoundedEscalation` & `IEscalationBudget`** — Finite escalation chains with strict attempt bounds.
* **`AgentTopology`** — Composable agent patterns: `SequentialAgent`, `ParallelAgent`, `LoopAgent`, and `CoordinatorAgent`.
* **`SemanticIntentRouter`** — Multi-tier intent routing (deterministic, lightweight, or frontier).
* **`BoundedAgentMemoryBank`** — Bounded episodic and working memory stores.
* **`RollingContextCompactor`** — In-flight conversation summarization and sliding window budgeting.

---

## 🌐 Protocol Wire Formats (`HFLabs.ML.Agentic.Protocols`)

Built-in, schema-pinned wire models with explicit `JsonPropertyName` bindings:

* **Model Context Protocol (MCP - Spec 2025-06-18):**
  * Full support for `McpTool`, `McpToolCallParams`, `McpToolCallResult`, `McpInitializeResult`, and standard MCP tool discovery.
* **Agent-to-Agent (A2A - Spec v0.2.5):**
  * `A2AMessage`, `A2APart`, `A2ATask`, `A2ATaskStatus`, `A2AArtifact`, and spec-compliant `A2AAgentCard`.
* **JSON-RPC 2.0:**
  * `JsonRpcRequest`, `JsonRpcResponse`, `JsonRpcError`, and `JsonRpcHttpClient`.

---

## 💻 Code Example: Composing an Agentic Pipeline

```csharp
using HFLabs.ML.Agentic;
using HFLabs.ML.Agentic.Protocols;

// 1. Set up a shared blackboard and budget ledger
var blackboard = new AgentBlackboard();
var ledger = new ResourceBudgetLedger(maxTokens: 100_000, maxCostDollars: 5.0m);

// 2. Wrap agent tasks into a parallel dependency DAG
var executor = new DependencyLayerExecutor<MyAgentTask, TaskOutcome>();

// 3. Add Human-In-The-Loop approval gate
var gate = new HumanInTheLoopGate(async (task, proposal) =>
{
    Console.WriteLine($"Review proposal for {task.Name}: {proposal}");
    return GateDecision.Approved;
});

// 4. Execute with deterministic verification
var verification = new ChainOfVerificationPipeline();
```

---

## 🧪 Verification & Test Suite

The library includes a comprehensive xUnit test suite validating all 21 patterns and wire protocols:

```bash
dotnet test src/HFLabs/Libraries/ML/ML.Agentic/tests/ML.Agentic.Tests.csproj
# Passed: 62, Failed: 0, Duration: 218 ms
```

---

## 📄 Licensing & Ecosystem

* **License:** Licensed under the **Apache-2.0** Open Source License.
* **Zero Dependency Guarantee:** Does not take dependencies on external cloud SDKs or heavy frameworks. Fits seamlessly into ASP.NET Core, WPF, MAUI, or console daemon services.

## Trademarks and non-affiliation

Product and company names in this repository belong to their owners and are used only to say what this project works with. HF Laboratories is not affiliated with, endorsed by, or sponsored by any of them.

- Microsoft, .NET, NuGet are trademarks or registered trademarks of Microsoft Corporation.
- FastMCP, Model Context Protocol (MCP) are names of third-party open-source projects and belong to their maintainers.

See [hflabs.dev/legal/trademarks](https://hflabs.dev/legal/trademarks) for the full list.
