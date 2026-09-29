namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A node in a Monte Carlo Tree Search exploration graph representing an explored thought or graph state.
/// </summary>
/// <typeparam name="TState">State representation type.</typeparam>
/// <typeparam name="TAction">Action or reasoning step type.</typeparam>
/// <param name="Id">Unique node identifier.</param>
/// <param name="State">Current state payload.</param>
/// <param name="ActionFromParent">Action taken from parent node to arrive at this state.</param>
/// <param name="ParentId">Identifier of parent node.</param>
/// <param name="ChildIds">Identifiers of child nodes.</param>
/// <param name="TotalReward">Accumulated reward backpropagated through this node.</param>
/// <param name="VisitCount">Number of times this node has been visited during search.</param>
public sealed record MctsNode<TState, TAction>(
    string Id,
    TState State,
    TAction? ActionFromParent,
    string? ParentId,
    List<string> ChildIds,
    double TotalReward = 0.0,
    int VisitCount = 0)
{
    /// <summary>
    /// Gets the mean empirical reward of this node.
    /// </summary>
    public double AverageReward => VisitCount > 0 ? TotalReward / VisitCount : 0.0;

    /// <summary>
    /// Computes the Upper Confidence Bound for Trees (UCB1) score balancing exploitation and exploration.
    /// </summary>
    /// <param name="parentVisits">Visit count of the parent node.</param>
    /// <param name="explorationConstant">Exploration bias parameter (typically sqrt(2) ~ 1.414).</param>
    /// <returns>UCB1 priority score.</returns>
    public double CalculateUcb1(int parentVisits, double explorationConstant = 1.414)
    {
        if (VisitCount == 0)
        {
            return double.MaxValue; // Unvisited nodes have infinite priority to ensure initial exploration
        }

        double exploitation = AverageReward;
        double exploration = explorationConstant * Math.Sqrt(Math.Log(Math.Max(1, parentVisits)) / VisitCount);
        return exploitation + exploration;
    }
}

/// <summary>
/// Defines the domain mechanics, action transitions, and terminal evaluation for an MCTS exploration problem.
/// </summary>
/// <typeparam name="TState">State type.</typeparam>
/// <typeparam name="TAction">Action type.</typeparam>
public interface IMctsProblem<TState, TAction>
{
    /// <summary>
    /// Generates legal candidate action transitions from the given state.
    /// </summary>
    /// <param name="state">Current state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of candidate (Action, NextState) pairs.</returns>
    Task<IReadOnlyList<(TAction Action, TState NextState)>> GenerateCandidateActionsAsync(
        TState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the terminal reward or quality of a state (typically between 0.0 and 1.0).
    /// </summary>
    /// <param name="state">Terminal state to score.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scalar reward score.</returns>
    Task<double> EvaluateTerminalStateAsync(
        TState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a state represents a terminal leaf or goal condition.
    /// </summary>
    /// <param name="state">State to inspect.</param>
    /// <returns>True if terminal; otherwise false.</returns>
    bool IsTerminal(TState state);
}

/// <summary>
/// Monte Carlo Tree Search engine exploring reasoning trajectories and hypothesis trees conforming to PAT-CS-262.
/// </summary>
/// <typeparam name="TState">State type.</typeparam>
/// <typeparam name="TAction">Action type.</typeparam>
public sealed class MctsExplorationEngine<TState, TAction>(
    IMctsProblem<TState, TAction> problem,
    double explorationConstant = 1.414)
{
    private readonly IMctsProblem<TState, TAction> _problem = problem ?? throw new ArgumentNullException(nameof(problem));
    private readonly double _explorationConstant = explorationConstant;

    /// <summary>
    /// Executes MCTS iterations starting from the initial root state and returns the highest-value child node.
    /// </summary>
    /// <param name="initialState">Root starting state.</param>
    /// <param name="iterations">Search iteration budget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The highest-reward child node from the root.</returns>
    public async Task<MctsNode<TState, TAction>> ExploreAsync(
        TState initialState,
        int iterations = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        int searchIterations = Math.Max(1, iterations);

        Dictionary<string, MctsNode<TState, TAction>> tree = [];
        var root = new MctsNode<TState, TAction>("root", initialState, default, null, []);
        tree[root.Id] = root;

        for (int iter = 0; iter < searchIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Selection: follow highest UCB1 down to frontier
            var current = root;
            while (current.ChildIds.Count > 0 && !_problem.IsTerminal(current.State))
            {
                current = current.ChildIds
                    .Select(id => tree[id])
                    .OrderByDescending(node => node.CalculateUcb1(current.VisitCount, _explorationConstant))
                    .First();
            }

            // 2. Expansion: generate children if non-terminal
            if (!_problem.IsTerminal(current.State) && current.ChildIds.Count == 0)
            {
                var candidates = await _problem.GenerateCandidateActionsAsync(current.State, cancellationToken).ConfigureAwait(false);
                foreach (var (act, nextState) in candidates)
                {
                    string childId = $"{current.Id}_c{current.ChildIds.Count}";
                    var childNode = new MctsNode<TState, TAction>(childId, nextState, act, current.Id, []);
                    tree[childId] = childNode;
                    current.ChildIds.Add(childId);
                }

                if (current.ChildIds.Count > 0)
                {
                    current = tree[current.ChildIds[0]];
                }
            }

            // 3. Rollout / Evaluation: score terminal quality
            double reward = await _problem.EvaluateTerminalStateAsync(current.State, cancellationToken).ConfigureAwait(false);

            // 4. Backpropagation: walk up to root updating visits and rewards
            var backpropNode = current;
            while (backpropNode != null)
            {
                tree[backpropNode.Id] = backpropNode with
                {
                    TotalReward = backpropNode.TotalReward + reward,
                    VisitCount = backpropNode.VisitCount + 1
                };

                backpropNode = backpropNode.ParentId != null ? tree[backpropNode.ParentId] : null;
            }

            root = tree["root"];
        }

        // Return best child of root by average reward
        if (root.ChildIds.Count > 0)
        {
            return root.ChildIds.Select(id => tree[id]).OrderByDescending(n => n.AverageReward).First();
        }

        return root;
    }
}
