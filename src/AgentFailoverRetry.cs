namespace HFLabs.ML.Agentic;

/// <summary>Retry budget and exponential backoff for <see cref="AgentFailoverRetry"/>.</summary>
public sealed class FailoverRetryOptions
{
    private int _maxAttempts = 3;

    /// <summary>Total attempts including the first (minimum 1).</summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set => _maxAttempts = Math.Max(1, value);
    }

    /// <summary>Delay before the second attempt; doubles for each further attempt.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan BackoffForAttempt(int attemptNumber) =>
        TimeSpan.FromMilliseconds(BaseBackoff.TotalMilliseconds * Math.Pow(2, Math.Max(0, attemptNumber - 1)));
}

/// <summary>Result of <see cref="AgentFailoverRetry.ExecuteAsync{TTarget,TResult}"/>.</summary>
/// <param name="Succeeded">True when an attempt returned without throwing.</param>
/// <param name="Result">The returned value when <paramref name="Succeeded"/>.</param>
/// <param name="Target">The target of the last attempt.</param>
/// <param name="Attempts">Attempts made.</param>
/// <param name="LastException">The last transport exception when every attempt threw.</param>
public sealed record FailoverRetryResult<TTarget, TResult>(bool Succeeded, TResult? Result, TTarget Target, int Attempts, Exception? LastException);

/// <summary>Hooks for observing <see cref="AgentFailoverRetry"/> without coupling it to an event model.</summary>
public sealed class FailoverRetryHooks<TTarget>
{
    /// <summary>Before each attempt: (target, attempt number).</summary>
    public Func<TTarget, int, CancellationToken, Task>? OnAttempting { get; init; }

    /// <summary>After an attempt throws: (target, attempt number, exception, whether another attempt follows).</summary>
    public Func<TTarget, int, Exception, bool, CancellationToken, Task>? OnAttemptFailed { get; init; }
}

/// <summary>
/// Retries an operation across a pool of targets. Only exceptions are retried -- a returned value, even one
/// describing a failure, is a semantic outcome and ends the loop. Each retry asks <c>selectTarget</c> for a
/// target while excluding the one that just failed (the selector may return it anyway when it is the only
/// option), with exponential backoff between attempts. Cancellation is never retried.
/// </summary>
public static class AgentFailoverRetry
{
    public static async Task<FailoverRetryResult<TTarget, TResult>> ExecuteAsync<TTarget, TResult>(
        Func<TTarget?, TTarget> selectTarget,
        Func<TTarget, CancellationToken, Task<TResult>> attempt,
        FailoverRetryOptions? options = null,
        FailoverRetryHooks<TTarget>? hooks = null,
        CancellationToken ct = default)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(selectTarget);
        ArgumentNullException.ThrowIfNull(attempt);
        options ??= new FailoverRetryOptions();

        TTarget? lastFailed = null;
        Exception? lastException = null;
        for (int n = 1; ; n++)
        {
            TTarget target = selectTarget(lastFailed);
            if (hooks?.OnAttempting is { } onAttempting)
            {
                await onAttempting(target, n, ct).ConfigureAwait(false);
            }

            try
            {
                TResult result = await attempt(target, ct).ConfigureAwait(false);
                return new FailoverRetryResult<TTarget, TResult>(true, result, target, n, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                lastFailed = target;
                bool more = n < options.MaxAttempts;
                if (hooks?.OnAttemptFailed is { } onFailed)
                {
                    await onFailed(target, n, ex, more, ct).ConfigureAwait(false);
                }

                if (!more)
                {
                    return new FailoverRetryResult<TTarget, TResult>(false, default, target, n, lastException);
                }

                TimeSpan delay = options.BackoffForAttempt(n);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
