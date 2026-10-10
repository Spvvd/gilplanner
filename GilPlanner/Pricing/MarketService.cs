using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GilPlanner.Pricing;

/// <summary>
/// Owns the latest Universalis data. Refreshes run in the background; readers (the UI) only ever see a
/// complete, immutable snapshot via <see cref="Data"/>.
/// </summary>
public sealed class MarketService(UniversalisClient client, IRecipeSource recipes) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? cts;

    /// <summary>How many of the cheapest listings on your world are fetched per tracked item.</summary>
    private const int WorldListingCount = 15;

    /// <summary>How many of the cheapest data center listings are fetched per material.</summary>
    private const int DcListingCount = 40;

    private volatile IReadOnlyDictionary<uint, ItemMarket> data = new Dictionary<uint, ItemMarket>();
    private volatile IReadOnlyDictionary<uint, ItemListings> worldListings = new Dictionary<uint, ItemListings>();
    private volatile IReadOnlyDictionary<uint, ItemListings> dcListings = new Dictionary<uint, ItemListings>();
    private volatile MarketFailure? failure;
    private volatile bool listingsFailed;
    private int consecutiveFailures;
    private long nextRetryTicks;
    private long lastUpdatedTicks;
    private int refreshing;
    private int version;

    public IReadOnlyDictionary<uint, ItemMarket> Data => data;

    /// <summary>Cheapest listings on your own world, for the tracked items (empty until fetched).</summary>
    public IReadOnlyDictionary<uint, ItemListings> WorldListings => worldListings;

    /// <summary>Cheapest listings across your data center, for the materials (empty until fetched).</summary>
    public IReadOnlyDictionary<uint, ItemListings> DcListings => dcListings;

    /// <summary>Why the last update failed, or null when it succeeded.</summary>
    public MarketFailure? Failure => failure;

    /// <summary>True when the prices were loaded but the detailed listings failed, so costs use lowest prices only.</summary>
    public bool ListingsFailed => listingsFailed;

    public int ConsecutiveFailures => Volatile.Read(ref consecutiveFailures);

    /// <summary>Earliest time an automatic refresh may try again after a failure.</summary>
    public DateTimeOffset? NextRetryAt
    {
        get
        {
            var ticks = Interlocked.Read(ref nextRetryTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Automatic refreshing stopped after too many failures in a row; a manual refresh is needed.</summary>
    public bool AutoRefreshPaused => ConsecutiveFailures >= RetryPolicy.MaxAutoFailures;

    public bool IsAutoRefreshBlocked(DateTimeOffset now) => AutoRefreshPaused || (NextRetryAt is { } next && now < next);

    public bool IsRefreshing => Volatile.Read(ref refreshing) != 0;

    /// <summary>Incremented whenever <see cref="Data"/> or the world it belongs to changes.</summary>
    public int Version => Volatile.Read(ref version);

    public uint WorldId { get; private set; }

    public DateTimeOffset? LastUpdated
    {
        get
        {
            var ticks = Interlocked.Read(ref lastUpdatedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Every item whose price is needed to cost <paramref name="trackedItems"/>, including materials.</summary>
    public HashSet<uint> CollectItemIds(IEnumerable<uint> trackedItems) => Collect(trackedItems).All;

    /// <summary>The materials (and materials of materials) of <paramref name="trackedItems"/>.</summary>
    public HashSet<uint> CollectMaterialIds(IEnumerable<uint> trackedItems) => Collect(trackedItems).Materials;

    private (HashSet<uint> All, HashSet<uint> Materials) Collect(IEnumerable<uint> trackedItems)
    {
        var shallowestDepth = new Dictionary<uint, int>();
        var materials = new HashSet<uint>();
        foreach (var id in trackedItems)
            Collect(id, 0, shallowestDepth, materials);
        return ([.. shallowestDepth.Keys], materials);
    }

    // Materials of materials are priced down to ProfitCalculator.MaxCraftDepth levels of sub-crafting.
    private void Collect(uint itemId, int depth, Dictionary<uint, int> shallowestDepth, HashSet<uint> materials)
    {
        if (depth > 0)
            materials.Add(itemId);

        if (shallowestDepth.TryGetValue(itemId, out var seenAt) && seenAt <= depth)
            return;

        shallowestDepth[itemId] = depth;
        if (depth > ProfitCalculator.MaxCraftDepth)
            return;

        foreach (var recipe in recipes.GetRecipes(itemId))
        {
            foreach (var ingredient in recipe.Ingredients)
                Collect(ingredient.ItemId, depth + 1, shallowestDepth, materials);
        }
    }

    /// <summary>
    /// Starts a background refresh, cancelling any refresh that is still running. Aggregated prices are published
    /// first; the detailed listings (used for quantity-aware costs and the robust sell price) follow.
    /// </summary>
    /// <param name="fetchWorldListings">Whether the cheapest listings on your own world are needed (robust sell price).</param>
    public void Refresh(string worldName, string dataCenterName, uint worldId, IEnumerable<uint> trackedItems, bool fetchWorldListings)
    {
        var tracked = trackedItems.ToList();
        var (ids, materials) = Collect(tracked);

        CancellationTokenSource mine;
        lock (sync)
        {
            cts?.Cancel();
            mine = cts = new CancellationTokenSource();
        }

        Interlocked.Exchange(ref refreshing, 1);

        // Taken while the lock is held, before Dispose can dispose the source.
        var token = mine.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var fetched = await client.FetchAsync(worldName, worldId, ids, token).ConfigureAwait(false);

                // Another refresh may have replaced this one while the request was in flight.
                if (!Publish(token, () => PublishPrices(worldId, fetched)))
                    return;

                await RefreshListingsAsync(worldName, dataCenterName, tracked, materials, fetchWorldListings, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced by a newer refresh or disposed; the newer one reports its own result.
            }
            catch (Exception ex)
            {
                Publish(token, () => RecordFailure(ex));
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(cts, mine))
                        Interlocked.Exchange(ref refreshing, 0);
                }
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="publish"/> unless this refresh has been replaced. Starting a new refresh cancels the old one
    /// under the same lock, so a replaced refresh can never overwrite what its successor published.
    /// </summary>
    private bool Publish(CancellationToken token, Action publish)
    {
        lock (sync)
        {
            if (token.IsCancellationRequested)
                return false;

            publish();
            return true;
        }
    }

    private void PublishPrices(uint worldId, Dictionary<uint, ItemMarket> fetched)
    {
        var sameWorld = WorldId == worldId;
        var merged = sameWorld
            ? new Dictionary<uint, ItemMarket>(data)
            : new Dictionary<uint, ItemMarket>();
        foreach (var (id, market) in fetched)
            merged[id] = market;

        if (!sameWorld)
        {
            worldListings = new Dictionary<uint, ItemListings>();
            dcListings = new Dictionary<uint, ItemListings>();
        }

        WorldId = worldId;
        data = merged;
        Interlocked.Exchange(ref lastUpdatedTicks, DateTimeOffset.UtcNow.Ticks);
        Interlocked.Increment(ref version);
    }

    private void RecordSuccess()
    {
        Interlocked.Exchange(ref consecutiveFailures, 0);
        Interlocked.Exchange(ref nextRetryTicks, 0);
        failure = null;
        listingsFailed = false;
        Interlocked.Increment(ref version);
    }

    private void RecordFailure(Exception ex)
    {
        var info = MarketFailure.Describe(ex);
        var failures = Interlocked.Increment(ref consecutiveFailures);
        Interlocked.Exchange(ref nextRetryTicks, (DateTimeOffset.UtcNow + RetryPolicy.NextDelay(failures, info.RetryAfter)).Ticks);
        failure = info;
        Interlocked.Increment(ref version);
    }

    private async Task RefreshListingsAsync(
        string worldName,
        string dataCenterName,
        List<uint> tracked,
        HashSet<uint> materials,
        bool fetchWorldListings,
        CancellationToken token)
    {
        try
        {
            var world = fetchWorldListings
                ? await client.FetchListingsAsync(worldName, tracked, WorldListingCount, token).ConfigureAwait(false)
                : new Dictionary<uint, ItemListings>();
            var dc = await client.FetchListingsAsync(dataCenterName, materials, DcListingCount, token).ConfigureAwait(false);

            Publish(token, () =>
            {
                worldListings = world;
                dcListings = dc;
                RecordSuccess();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The aggregated prices are already published, so the table still works with lowest prices only. Listings
            // from an earlier refresh are dropped so they are never mixed with these newer prices.
            Publish(token, () =>
            {
                worldListings = new Dictionary<uint, ItemListings>();
                dcListings = new Dictionary<uint, ItemListings>();
                listingsFailed = true;
                RecordFailure(ex);
            });
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
