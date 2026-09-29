namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Defines an individual evaluation criterion within an assessment rubric.
/// </summary>
/// <param name="Name">Criterion name (e.g., "Grounding", "Accuracy", "Completeness").</param>
/// <param name="Description">Guideline explaining the scoring basis.</param>
/// <param name="Weight">Relative weight in the composite score.</param>
/// <param name="PassingThreshold">Minimum score required to pass (0.0 to 1.0).</param>
public sealed record RubricCriterion(
    string Name,
    string Description,
    double Weight,
    double PassingThreshold = 0.7);

/// <summary>
/// The evaluated score and rationale for a single rubric dimension.
/// </summary>
/// <param name="CriterionName">Name of the evaluated criterion.</param>
/// <param name="Score">Normalized score between 0.0 and 1.0.</param>
/// <param name="Passed">Whether the score met or exceeded the passing threshold.</param>
/// <param name="Rationale">Explanatory justification for the assigned score.</param>
public sealed record DimensionScore(
    string CriterionName,
    double Score,
    bool Passed,
    string Rationale);

/// <summary>
/// Comprehensive multi-dimensional evaluation scorecard.
/// </summary>
/// <param name="CompositeScore">Normalized weighted average score (0.0 to 1.0).</param>
/// <param name="AllCriteriaPassed">True if every individual criterion met its passing threshold.</param>
/// <param name="DimensionScores">Detailed scores for each criterion.</param>
/// <param name="OverallSummary">Composite qualitative assessment.</param>
public sealed record EvaluationScorecard(
    double CompositeScore,
    bool AllCriteriaPassed,
    IReadOnlyList<DimensionScore> DimensionScores,
    string OverallSummary);

/// <summary>
/// Rubric aggregating individual criteria and calculating weighted scorecards conforming to PAT-CS-260.
/// </summary>
public sealed class EvaluationRubric(IReadOnlyList<RubricCriterion> criteria)
{
    /// <summary>
    /// Gets the criteria defined in this rubric.
    /// </summary>
    public IReadOnlyList<RubricCriterion> Criteria { get; } = criteria ?? throw new ArgumentNullException(nameof(criteria));

    /// <summary>
    /// Compiles a composite scorecard from evaluated dimension scores.
    /// </summary>
    /// <param name="dimensionScores">Dimension score results.</param>
    /// <param name="summary">Overall qualitative summary.</param>
    /// <returns>A structured evaluation scorecard.</returns>
    public EvaluationScorecard CompileScorecard(IReadOnlyList<DimensionScore> dimensionScores, string summary)
    {
        ArgumentNullException.ThrowIfNull(dimensionScores);
        ArgumentNullException.ThrowIfNull(summary);

        double totalWeight = Criteria.Sum(c => c.Weight);
        double weightedSum = 0.0;
        bool allPassed = true;

        for (int i = 0; i < Criteria.Count; i++)
        {
            var criterion = Criteria[i];
            var match = dimensionScores.FirstOrDefault(s => s.CriterionName.Equals(criterion.Name, StringComparison.OrdinalIgnoreCase));
            double score = match?.Score ?? 0.0;
            weightedSum += score * criterion.Weight;

            if (score < criterion.PassingThreshold)
            {
                allPassed = false;
            }
        }

        double composite = totalWeight > 0.0 ? weightedSum / totalWeight : 0.0;
        return new EvaluationScorecard(composite, allPassed, dimensionScores, summary);
    }
}

/// <summary>
/// Contract for an automated judge assessing agent outputs against rubrics conforming to PAT-CS-260.
/// </summary>
/// <typeparam name="TSubject">Candidate output type under review.</typeparam>
/// <typeparam name="TContext">Reference context against which output is judged.</typeparam>
public interface IAgentJudge<in TSubject, in TContext>
{
    /// <summary>
    /// Evaluates the candidate output against the configured rubric and context.
    /// </summary>
    /// <param name="subject">The output candidate to evaluate.</param>
    /// <param name="context">Reference ground truth or task context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Evaluation scorecard.</returns>
    Task<EvaluationScorecard> EvaluateAsync(
        TSubject subject,
        TContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes multi-dimensional rubric evaluations by delegating criterion scoring to evaluator delegates.
/// </summary>
/// <typeparam name="TSubject">Subject type.</typeparam>
/// <typeparam name="TContext">Context type.</typeparam>
public sealed class AgentJudgeEvaluator<TSubject, TContext>(
    EvaluationRubric rubric,
    Func<TSubject, RubricCriterion, TContext, CancellationToken, Task<DimensionScore>> dimensionScorer)
    : IAgentJudge<TSubject, TContext>
{
    private readonly EvaluationRubric _rubric = rubric ?? throw new ArgumentNullException(nameof(rubric));
    private readonly Func<TSubject, RubricCriterion, TContext, CancellationToken, Task<DimensionScore>> _dimensionScorer =
        dimensionScorer ?? throw new ArgumentNullException(nameof(dimensionScorer));

    /// <inheritdoc/>
    public async Task<EvaluationScorecard> EvaluateAsync(
        TSubject subject,
        TContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        List<DimensionScore> scores = [];

        for (int i = 0; i < _rubric.Criteria.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var criterion = _rubric.Criteria[i];
            var score = await _dimensionScorer(subject, criterion, context, cancellationToken).ConfigureAwait(false);
            scores.Add(score);
        }

        string summary = scores.All(s => s.Passed)
            ? "Candidate met or exceeded all rubric criteria."
            : $"Candidate failed {scores.Count(s => !s.Passed)} criteria.";

        return _rubric.CompileScorecard(scores, summary);
    }
}
