using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// Counts federation's changes (the engine's events, federation turned on or
/// off), for the configuration page to wait on and reload at once.
/// </summary>
public sealed class Changes
{
    private readonly object _lock = new();
    private long _seq;
    private TaskCompletionSource? _next;

    /// <summary>The count now.</summary>
    public long Seq
    {
        get
        {
            lock (_lock)
            {
                return _seq;
            }
        }
    }

    /// <summary>Counts a change, and wakes those waiting.</summary>
    public void Bump()
    {
        TaskCompletionSource? next;
        lock (_lock)
        {
            _seq++;
            next = _next;
            _next = null;
        }

        next?.TrySetResult();
    }

    /// <summary>Waits until the count is past after, or the wait ends, and says the count then.</summary>
    /// <param name="after">The count the page last saw.</param>
    /// <param name="wait">How long to wait at most.</param>
    /// <param name="ct">Ends the wait (the page gone).</param>
    /// <returns>The count.</returns>
    public async Task<long> WaitAsync(long after, TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        while (true)
        {
            Task next;
            lock (_lock)
            {
                if (_seq != after)
                {
                    return _seq;
                }

                _next ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                next = _next.Task;
            }

            try
            {
                await next.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Seq;
            }
        }
    }
}
