namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

/// <summary>
/// Status of an individual milestone within an agent goal hierarchy.
/// </summary>
public enum MilestoneStatus
{
    /// <summary>
    /// Milestone has not been started.
    /// </summary>
    NotStarted,

    /// <summary>
    /// Milestone is actively in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// Milestone criteria have been successfully achieved.
    /// </summary>
    Achieved,

    /// <summary>
    /// Milestone execution has stalled or timed out.
    /// </summary>
    Stalled
}

/// <summary>
/// A measurable milestone node within an agent goal tree.
/// </summary>
/// <param name="Id">Unique milestone identifier.</param>
/// <param name="Name">Descriptive name of milestone objective.</param>
/// <param name="Weight">Relative weight of milestone towards total goal completion.</param>
/// <param name="Status">Current milestone status.</param>
/// <param name="AchievedAt">UTC timestamp when milestone was achieved.</param>
public sealed record AgentMilestone(
    string Id,
    string Name,
    double Weight,
    MilestoneStatus Status = MilestoneStatus.NotStarted,
    DateTime? AchievedAt = null);

/// <summary>
/// Point-in-time telemetry snapshot of agent progress across a goal hierarchy.
/// </summary>
/// <param name="GoalId">Target goal identifier.</param>
/// <param name="ProgressPercentage">Weighted completion percentage (0.0 to 100.0).</param>
/// <param name="CompletedMilestones">Count of achieved milestones.</param>
/// <param name="TotalMilestones">Total milestone count.</param>
/// <param name="IsStalled">Whether progress velocity has stalled past configured threshold.</param>
/// <param name="ElapsedSinceLastMilestone">Time elapsed since the last milestone completion or progress pulse.</param>
public sealed record GoalProgressSnapshot(
    string GoalId,
    double ProgressPercentage,
    int CompletedMilestones,
    int TotalMilestones,
    bool IsStalled,
    TimeSpan ElapsedSinceLastMilestone);

/// <summary>
/// Telemetry monitor and sentinel tracking milestone completion and detecting execution stalls conforming to PAT-CS-258.
/// </summary>
public sealed class GoalTelemetrySentinel(TimeSpan stallTimeout)
{
    private readonly List<AgentMilestone> _milestones = [];
    private readonly TimeSpan _stallTimeout = stallTimeout > TimeSpan.Zero ? stallTimeout : TimeSpan.FromSeconds(30);
    private readonly Lock _sync = new();
    private DateTime _lastProgressTimestamp = DateTime.UtcNow;

    /// <summary>
    /// Adds a milestone to the monitored goal hierarchy.
    /// </summary>
    /// <param name="milestone">Milestone to add.</param>
    public void AddMilestone(AgentMilestone milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        lock (_sync)
        {
            _milestones.Add(milestone);
        }
    }

    /// <summary>
    /// Marks a milestone as achieved, recording the completion timestamp and resetting the stall timer.
    /// </summary>
    /// <param name="milestoneId">The ID of the achieved milestone.</param>
    public void MarkAchieved(string milestoneId)
    {
        ArgumentNullException.ThrowIfNull(milestoneId);
        lock (_sync)
        {
            for (int i = 0; i < _milestones.Count; i++)
            {
                if (_milestones[i].Id.Equals(milestoneId, StringComparison.OrdinalIgnoreCase))
                {
                    _milestones[i] = _milestones[i] with
                    {
                        Status = MilestoneStatus.Achieved,
                        AchievedAt = DateTime.UtcNow
                    };
                    _lastProgressTimestamp = DateTime.UtcNow;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Pulses the progress sentinel to indicate active work, resetting the stall window.
    /// </summary>
    public void PulseProgress()
    {
        lock (_sync)
        {
            _lastProgressTimestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Evaluates current weighted goal progress and determines whether a stall condition exists.
    /// </summary>
    /// <param name="goalId">Goal identifier being evaluated.</param>
    /// <returns>A progress snapshot.</returns>
    public GoalProgressSnapshot EvaluateProgress(string goalId)
    {
        ArgumentNullException.ThrowIfNull(goalId);

        lock (_sync)
        {
            double totalWeight = _milestones.Sum(m => m.Weight);
            double achievedWeight = _milestones
                .Where(m => m.Status == MilestoneStatus.Achieved)
                .Sum(m => m.Weight);

            double percentage = totalWeight > 0.0
                ? (achievedWeight / totalWeight) * 100.0
                : 0.0;

            var elapsed = DateTime.UtcNow - _lastProgressTimestamp;
            bool stalled = elapsed > _stallTimeout && percentage < 100.0;

            int completedCount = _milestones.Count(m => m.Status == MilestoneStatus.Achieved);

            return new GoalProgressSnapshot(
                goalId,
                Math.Min(100.0, percentage),
                completedCount,
                _milestones.Count,
                stalled,
                elapsed);
        }
    }
}
