using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GilPlanner.Pricing;

/// <summary>
/// Scans the sales velocity of many items on one world in the background and keeps the ranked result. Readers (the
/// UI) only see complete, immutable snapshots.
/// </summary>
public sealed class PopularService(UniversalisClient client, Action<PopularScan>? onScanned = null) : IDisposable
{
    private const int Concurrency = 3;

    private readonly object sync = new();
    private CancellationTokenSource? cts;

    private volatile IReadOnlyList<PopularItem> results = [];
    private volatile MarketFailure? failure;
    private long lastScannedTicks;
    private int scanning;
    private int done;
    private int total;
    private int version;

    public IReadOnlyList<PopularItem> Results => results;

    /// <summary>Why the last scan failed, or null when it succeeded. Older results stay available.</summary>
    public MarketFailure? Failure => failure;

    public bool IsScanning => Volatile.Read(ref scanning) != 0;

    public (int Done, int Total) Progress => (Volatile.Read(ref done), Volatile.Read(ref total));

    public int Version => Volatile.Read(ref version);

    public uint WorldId { get; private set; }

    public DateTimeOffset? LastScanned
    {
        get
        {
            var ticks = Interlocked.Read(ref lastScannedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Shows a scan loaded from the cache without touching the network.</summary>
    public void Restore(PopularScan scan)
    {
        WorldId = scan.WorldId;
        results = scan.Items;
        failure = null;
        Interlocked.Exchange(ref lastScannedTicks, scan.ScannedAt.UtcTicks);
        Interlocked.Increment(ref version);
    }

    public void Scan(string worldName, uint worldId, IReadOnlyCollection<uint> itemIds)
    {
        CancellationTokenSource mine;
        lock (sync)
        {
            cts?.Cancel();
            mine = cts = new CancellationTokenSource();
        }

        Interlocked.Exchange(ref done, 0);
        Interlocked.Exchange(ref total, 0);
        Interlocked.Exchange(ref scanning, 1);

        _ = Task.Run(async () =>
        {
            try
            {
                var fetched = await client.FetchAsync(
                    worldName,
                    worldId,
                    itemIds,
                    mine.Token,
                    Concurrency,
                    (d, t) =>
                    {
                        Volatile.Write(ref done, d);
                        Volatile.Write(ref total, t);
                    }).ConfigureAwait(false);

                if (mine.IsCancellationRequested)
                    return;

                var scan = new PopularScan(worldId, DateTimeOffset.UtcNow, PopularityRanker.Rank(fetched.Values));
                Restore(scan);
                NotifyScanned(scan);
            }
            catch (OperationCanceledException) when (mine.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = MarketFailure.Describe(ex);
                Interlocked.Increment(ref version);
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(cts, mine))
                        Interlocked.Exchange(ref scanning, 0);
                }
            }
        });
    }

    // The results are already on screen; failing to save them to the cache must not make the scan look failed.
    private void NotifyScanned(PopularScan scan)
    {
        try
        {
            onScanned?.Invoke(scan);
        }
        catch (Exception)
        {
            // Saving the cache is only an optimisation.
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        }
    }
}
