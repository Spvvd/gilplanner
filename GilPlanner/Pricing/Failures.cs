using System;
using System.Net.Http;
using System.Text.Json;

namespace GilPlanner.Pricing;

public enum FailureKind
{
    RateLimited,
    ServerError,
    Network,
    Timeout,
    Other,
}

/// <summary>A Universalis request that kept failing after all retries.</summary>
public sealed class UniversalisException(FailureKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public FailureKind Kind { get; } = kind;

    /// <summary>How long the server asked us to wait, when it said so.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A failed update, described for the user. <see cref="Message"/> is plain language; <see cref="Detail"/> is technical.</summary>
public sealed record MarketFailure(FailureKind Kind, string Message, string Detail, TimeSpan? RetryAfter)
{
    public static MarketFailure Describe(Exception ex) => ex switch
    {
        UniversalisException u => new MarketFailure(u.Kind, Plain(u.Kind), u.Message, u.RetryAfter),
        HttpRequestException h => new MarketFailure(FailureKind.Network, Plain(FailureKind.Network), h.Message, null),
        OperationCanceledException o => new MarketFailure(FailureKind.Timeout, Plain(FailureKind.Timeout), o.Message, null),
        JsonException j => new MarketFailure(FailureKind.Other, "Universalis sent a response the plugin could not read", j.Message, null),
        _ => new MarketFailure(FailureKind.Other, "Something went wrong while loading prices", ex.Message, null),
    };

    private static string Plain(FailureKind kind) => kind switch
    {
        FailureKind.RateLimited => "Universalis is rate-limiting requests",
        FailureKind.ServerError => "Universalis is having server problems",
        FailureKind.Network => "Universalis could not be reached (no connection?)",
        FailureKind.Timeout => "Universalis took too long to answer",
        _ => "Universalis returned an unexpected answer",
    };
}

/// <summary>When to try again after failures, and when to stop trying on our own.</summary>
public static class RetryPolicy
{
    /// <summary>After this many failures in a row automatic refreshing pauses until the user refreshes manually.</summary>
    public const int MaxAutoFailures = 5;

    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxServerRequestedDelay = TimeSpan.FromMinutes(30);

    /// <summary>1, 2, 4, 8, then 15 minutes; a longer Retry-After from the server wins (up to 30 minutes).</summary>
    public static TimeSpan NextDelay(int consecutiveFailures, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var delay = TimeSpan.FromMinutes(Math.Pow(2, exponent));
        if (delay > MaxDelay)
            delay = MaxDelay;

        if (retryAfter is { } requested && requested > delay)
            delay = requested > MaxServerRequestedDelay ? MaxServerRequestedDelay : requested;

        return delay;
    }
}

public enum Freshness
{
    Fresh,
    Stale,
    VeryStale,
}

/// <summary>How old prices may get before they are flagged.</summary>
public static class DataFreshness
{
    private static readonly TimeSpan MinStaleWithAutoRefresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleWithoutAutoRefresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinVeryStale = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Stale means at least one scheduled refresh was missed (twice the refresh interval, at least 10 minutes).
    /// Without auto-refresh data is expected to age, so the limit is a flat 30 minutes. Very stale is three times that, at least 30 minutes.
    /// </summary>
    public static Freshness Classify(TimeSpan age, bool autoRefresh, TimeSpan refreshInterval)
    {
        var staleAfter = autoRefresh
            ? Max(refreshInterval * 2, MinStaleWithAutoRefresh)
            : StaleWithoutAutoRefresh;
        var veryStaleAfter = Max(staleAfter * 3, MinVeryStale);

        return age > veryStaleAfter ? Freshness.VeryStale : age > staleAfter ? Freshness.Stale : Freshness.Fresh;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static readonly TimeSpan UploadStaleAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan UploadVeryStaleAfter = TimeSpan.FromHours(48);

    /// <summary>
    /// How trustworthy a world's market data is for an item, by the time since players last uploaded it to Universalis.
    /// This is about the community data, not about when this plugin last asked: under 6 hours is fine, over 2 days is
    /// likely a ghost price.
    /// </summary>
    public static Freshness ClassifyUpload(TimeSpan age) =>
        age > UploadVeryStaleAfter ? Freshness.VeryStale : age > UploadStaleAfter ? Freshness.Stale : Freshness.Fresh;
}
