using System;

namespace GilPlanner.Pricing;

/// <summary>
/// Turns a burst of "please refresh" requests into one refresh: it fires once no new request has arrived for
/// <c>quietPeriod</c>, or <c>maxWait</c> after the first request of the burst, so steady requests cannot postpone it forever.
/// </summary>
public sealed class RefreshDebouncer(TimeSpan quietPeriod, TimeSpan maxWait)
{
    private DateTimeOffset? firstRequest;
    private DateTimeOffset? lastRequest;

    public bool IsPending => firstRequest is not null;

    public void Request(DateTimeOffset now)
    {
        firstRequest ??= now;
        lastRequest = now;
    }

    /// <summary>True exactly once when the pending burst is due; the request is then cleared.</summary>
    public bool TryTake(DateTimeOffset now)
    {
        if (firstRequest is not { } first || lastRequest is not { } last)
            return false;

        if (now < last + quietPeriod && now < first + maxWait)
            return false;

        firstRequest = null;
        lastRequest = null;
        return true;
    }
}
