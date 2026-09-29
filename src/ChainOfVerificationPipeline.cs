namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents a targeted verification inquiry formulated to confirm or refute a specific factual assertion.
/// </summary>
/// <param name="Id">Unique identifier for the verification question.</param>
/// <param name="Question">The factual question to execute against trusted knowledge sources.</param>
/// <param name="TargetClaim">The specific assertion in the draft answer being audited.</param>
public sealed record VerificationQuestion(
    string Id,
    string Question,
    string TargetClaim);

/// <summary>
/// The outcome of evaluating an isolated verification inquiry against verified ground truth.
/// </summary>
/// <param name="QuestionId">Identifier of the evaluated question.</param>
/// <param name="Verified">Whether the claim was corroborated by evidence.</param>
/// <param name="Evidence">Textual or entity citation supporting the verdict.</param>
/// <param name="Confidence">Evaluation confidence score between 0.0 and 1.0.</param>
public sealed record VerificationFactCheck(
    string QuestionId,
    bool Verified,
    string Evidence,
    double Confidence);

/// <summary>
/// Full audit envelope of a Chain-of-Verification execution cycle.
/// </summary>
/// <typeparam name="TAnswer">Answer payload type.</typeparam>
/// <param name="VerifiedAnswer">The final fact-grounded answer synthesized after verification.</param>
/// <param name="DraftAnswer">The initial draft response prior to verification.</param>
/// <param name="FactChecks">All individual fact-check outcomes and citations.</param>
/// <param name="OverallConfidence">Composite fact-grounding confidence ratio.</param>
public sealed record CoVeResult<TAnswer>(
    TAnswer VerifiedAnswer,
    TAnswer DraftAnswer,
    IReadOnlyList<VerificationFactCheck> FactChecks,
    double OverallConfidence);

/// <summary>
/// Contract for 4-stage Chain-of-Verification hallucination elimination pipelines conforming to PAT-CS-253.
/// </summary>
/// <typeparam name="TContext">Context input type.</typeparam>
/// <typeparam name="TAnswer">Answer output type.</typeparam>
public interface ICoVePipeline<in TContext, TAnswer>
{
    /// <summary>
    /// Generates draft, audits propositions against ground truth in isolation, and synthesizes a verified answer.
    /// </summary>
    /// <param name="context">Task and reference context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A verified answer envelope with full fact-checking audit trail.</returns>
    Task<CoVeResult<TAnswer>> VerifyAndGroundAsync(
        TContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Decoupled 4-stage Chain-of-Verification engine conforming to PAT-CS-253.
/// </summary>
/// <typeparam name="TContext">Input context type.</typeparam>
/// <typeparam name="TAnswer">Output answer type.</typeparam>
public sealed class ChainOfVerificationPipeline<TContext, TAnswer>(
    Func<TContext, CancellationToken, Task<TAnswer>> draftGenerator,
    Func<TAnswer, TContext, CancellationToken, Task<IReadOnlyList<VerificationQuestion>>> questionExtractor,
    Func<VerificationQuestion, TContext, CancellationToken, Task<VerificationFactCheck>> factVerifier,
    Func<TAnswer, IReadOnlyList<VerificationFactCheck>, TContext, CancellationToken, Task<TAnswer>> groundedSynthesizer)
    : ICoVePipeline<TContext, TAnswer>
{
    private readonly Func<TContext, CancellationToken, Task<TAnswer>> _draftGenerator = draftGenerator ?? throw new ArgumentNullException(nameof(draftGenerator));
    private readonly Func<TAnswer, TContext, CancellationToken, Task<IReadOnlyList<VerificationQuestion>>> _questionExtractor = questionExtractor ?? throw new ArgumentNullException(nameof(questionExtractor));
    private readonly Func<VerificationQuestion, TContext, CancellationToken, Task<VerificationFactCheck>> _factVerifier = factVerifier ?? throw new ArgumentNullException(nameof(factVerifier));
    private readonly Func<TAnswer, IReadOnlyList<VerificationFactCheck>, TContext, CancellationToken, Task<TAnswer>> _groundedSynthesizer = groundedSynthesizer ?? throw new ArgumentNullException(nameof(groundedSynthesizer));

    /// <inheritdoc/>
    public async Task<CoVeResult<TAnswer>> VerifyAndGroundAsync(TContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Stage 1: Generate initial draft
        TAnswer draft = await _draftGenerator(context, cancellationToken).ConfigureAwait(false);

        // Stage 2: Extract targeted verification questions
        var questions = await _questionExtractor(draft, context, cancellationToken).ConfigureAwait(false);

        // Stage 3: Execute independent factual verification in parallel
        var factTasks = questions.Select(q => _factVerifier(q, context, cancellationToken)).ToList();
        VerificationFactCheck[] factChecks = await Task.WhenAll(factTasks).ConfigureAwait(false);

        // Compute overall confidence
        double overallConfidence = factChecks.Length > 0
            ? factChecks.Count(c => c.Verified) / (double)factChecks.Length
            : 1.0;

        // Stage 4: Synthesize grounded response using verified evidence
        TAnswer verified = await _groundedSynthesizer(draft, factChecks, context, cancellationToken).ConfigureAwait(false);

        return new CoVeResult<TAnswer>(verified, draft, factChecks, overallConfidence);
    }
}
