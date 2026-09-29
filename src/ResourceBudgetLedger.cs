namespace HFLabs.ML.Agentic;

using System;
using System.Threading;

/// <summary>
/// Telemetry snapshot of resource consumption and remaining quota headroom.
/// </summary>
/// <param name="TokensUsed">Tokens consumed so far.</param>
/// <param name="MaxTokens">Maximum token ceiling.</param>
/// <param name="CostUsedUsd">Estimated cost in USD consumed so far.</param>
/// <param name="MaxCostUsd">Maximum financial cost ceiling.</param>
/// <param name="ToolCallsMade">Number of tool invocations performed.</param>
/// <param name="MaxToolCalls">Maximum allowed tool invocations.</param>
/// <param name="Elapsed">Elapsed wall-clock execution time.</param>
/// <param name="MaxLatency">Maximum allowed wall-clock latency.</param>
public sealed record ResourceBudgetSnapshot(
    long TokensUsed,
    long MaxTokens,
    double CostUsedUsd,
    double MaxCostUsd,
    int ToolCallsMade,
    int MaxToolCalls,
    TimeSpan Elapsed,
    TimeSpan MaxLatency)
{
    /// <summary>
    /// Gets the fractional utilization of the token budget (0.0 to 1.0+).
    /// </summary>
    public double TokenUtilization => MaxTokens > 0 ? (double)TokensUsed / MaxTokens : 0.0;

    /// <summary>
    /// Gets the fractional utilization of the financial budget (0.0 to 1.0+).
    /// </summary>
    public double CostUtilization => MaxCostUsd > 0.0 ? CostUsedUsd / MaxCostUsd : 0.0;
}

/// <summary>
/// Monitors and enforces multi-dimensional token, financial cost, latency, and tool budgets conforming to PAT-CS-263.
/// </summary>
public sealed class ResourceBudgetLedger(
    long maxTokens = 100_000,
    double maxCostUsd = 1.00,
    int maxToolCalls = 20,
    TimeSpan? maxLatency = null)
{
    private long _tokensUsed;
    private double _costUsedUsd;
    private int _toolCallsMade;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly TimeSpan _maxLatency = maxLatency ?? TimeSpan.FromMinutes(2);
    private readonly Lock _sync = new();
    private int _consecutiveDiminishingReturnsCount;

    /// <summary>
    /// Records incremental consumption of tokens, cost, and tool calls.
    /// </summary>
    /// <param name="tokens">Tokens consumed.</param>
    /// <param name="costUsd">Cost in USD.</param>
    /// <param name="toolCalls">Tool calls executed.</param>
    public void RecordConsumption(long tokens, double costUsd = 0.0, int toolCalls = 0)
    {
        lock (_sync)
        {
            _tokensUsed += Math.Max(0, tokens);
            _costUsedUsd += Math.Max(0.0, costUsd);
            _toolCallsMade += Math.Max(0, toolCalls);
        }
    }

    /// <summary>
    /// Checks whether all resource dimensions remain strictly within configured ceilings.
    /// </summary>
    /// <returns>True if budget remains; false if any limit has been exhausted.</returns>
    public bool HasBudgetRemaining()
    {
        lock (_sync)
        {
            if (_tokensUsed >= maxTokens) return false;
            if (_costUsedUsd >= maxCostUsd) return false;
            if (_toolCallsMade >= maxToolCalls) return false;
            if (DateTime.UtcNow - _startTime >= _maxLatency) return false;
            return true;
        }
    }

    /// <summary>
    /// Checks whether token or financial consumption has exceeded a warning watermark threshold (default 75%).
    /// </summary>
    /// <param name="thresholdRatio">Fractional watermark (e.g. 0.75 for 75%).</param>
    /// <returns>True if consumption exceeded watermark.</returns>
    public bool IsWarningWatermarkExceeded(double thresholdRatio = 0.75)
    {
        lock (_sync)
        {
            double tokenRatio = maxTokens > 0 ? (double)_tokensUsed / maxTokens : 0.0;
            double costRatio = maxCostUsd > 0.0 ? _costUsedUsd / maxCostUsd : 0.0;
            return tokenRatio >= thresholdRatio || costRatio >= thresholdRatio;
        }
    }

    /// <summary>
    /// Evaluates whether marginal gain between successive iterations has fallen below an early-stop epsilon.
    /// </summary>
    /// <param name="marginalGain">Observed quality or fitness improvement delta.</param>
    /// <param name="epsilon">Minimum acceptable delta (default 0.01).</param>
    /// <param name="patience">Number of consecutive low-gain iterations before signaling early stop.</param>
    /// <returns>True if early stopping should be triggered.</returns>
    public bool CheckDiminishingReturns(double marginalGain, double epsilon = 0.01, int patience = 2)
    {
        lock (_sync)
        {
            if (marginalGain < epsilon)
            {
                _consecutiveDiminishingReturnsCount++;
                if (_consecutiveDiminishingReturnsCount >= patience)
                {
                    return true;
                }
            }
            else
            {
                _consecutiveDiminishingReturnsCount = 0;
            }

            return false;
        }
    }

    /// <summary>
    /// Takes a point-in-time snapshot of ledger utilization.
    /// </summary>
    /// <returns>Resource budget snapshot.</returns>
    public ResourceBudgetSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new ResourceBudgetSnapshot(
                _tokensUsed,
                maxTokens,
                _costUsedUsd,
                maxCostUsd,
                _toolCallsMade,
                maxToolCalls,
                DateTime.UtcNow - _startTime,
                _maxLatency);
        }
    }
}
