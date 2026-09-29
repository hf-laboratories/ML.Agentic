namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A structured argument or critique contributed by a debate persona during a dialectic round.
/// </summary>
/// <param name="PersonaName">Name of the submitting persona.</param>
/// <param name="Round">Dialectic round index.</param>
/// <param name="Thesis">Primary claim or proposed solution.</param>
/// <param name="Rationale">Supporting theoretical or empirical rationale.</param>
/// <param name="Critiques">Critiques of proposals from prior rounds or competing perspectives.</param>
public sealed record DebateArgument(
    string PersonaName,
    int Round,
    string Thesis,
    string Rationale,
    IReadOnlyList<string> Critiques);

/// <summary>
/// The final synthesis or arbitration rendered by a neutral debate judge.
/// </summary>
/// <param name="ConsensusAnswer">The synthesized consensus answer.</param>
/// <param name="Confidence">Consensus confidence score between 0.0 and 1.0.</param>
/// <param name="Rationale">Explanatory synthesis of why this conclusion was reached.</param>
/// <param name="DissentingPoints">Key unresolved objections or minority perspectives noted for transparency.</param>
public sealed record DebateVerdict(
    string ConsensusAnswer,
    double Confidence,
    string Rationale,
    IReadOnlyList<string> DissentingPoints);

/// <summary>
/// Contract for a specialized debate persona offering distinct cognitive biases or domain expertise.
/// </summary>
public interface IDebatePersona
{
    /// <summary>
    /// Gets the unique name of the persona.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the domain lens or perspective represented by the persona (e.g., "Security Auditor", "Performance Optimizer").
    /// </summary>
    string Perspective { get; }

    /// <summary>
    /// Formulates an argument or counter-rebuttal given the debate topic and historical arguments.
    /// </summary>
    /// <param name="topic">The core problem or question under debate.</param>
    /// <param name="previousArguments">All arguments exchanged in prior rounds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured debate argument.</returns>
    Task<DebateArgument> FormulateArgumentAsync(
        string topic,
        IReadOnlyList<DebateArgument> previousArguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract for a neutral judge arbitrating multi-persona debates and synthesizing consensus.
/// </summary>
public interface IDebateJudge
{
    /// <summary>
    /// Evaluates the complete debate transcript and renders a consensus verdict.
    /// </summary>
    /// <param name="topic">The debated topic.</param>
    /// <param name="debateHistory">Full sequence of arguments contributed by all personas.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A binding consensus verdict.</returns>
    Task<DebateVerdict> ArbitrateAsync(
        string topic,
        IReadOnlyList<DebateArgument> debateHistory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates multi-round dialectics among diverse personas and synthesizes consensus via an arbiter conforming to PAT-CS-254.
/// </summary>
public sealed class MultiPersonaDebateCoordinator(
    IReadOnlyList<IDebatePersona> personas,
    IDebateJudge judge,
    int rounds = 2)
{
    private readonly IReadOnlyList<IDebatePersona> _personas = personas ?? throw new ArgumentNullException(nameof(personas));
    private readonly IDebateJudge _judge = judge ?? throw new ArgumentNullException(nameof(judge));
    private readonly int _rounds = Math.Max(1, rounds);

    /// <summary>
    /// Executes the multi-round debate and returns the judge's consensus verdict along with the full transcript.
    /// </summary>
    /// <param name="topic">The question, design proposal, or hypothesis to debate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the final verdict and the full transcript of arguments.</returns>
    public async Task<(DebateVerdict Verdict, IReadOnlyList<DebateArgument> Transcript)> ConductDebateAsync(
        string topic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);

        List<DebateArgument> transcript = [];

        for (int round = 1; round <= _rounds; round++)
        {
            foreach (var persona in _personas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var argument = await persona.FormulateArgumentAsync(topic, transcript, cancellationToken).ConfigureAwait(false);
                transcript.Add(argument);
            }
        }

        var verdict = await _judge.ArbitrateAsync(topic, transcript, cancellationToken).ConfigureAwait(false);
        return (verdict, transcript);
    }
}
