using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GilPlanner.Pricing;

namespace GilPlanner;

/// <summary>Ties the tracked-item list, market data and profit calculation together for the UI.</summary>
public sealed class Planner(
    Configuration configuration,
    IDalamudPluginInterface pluginInterface,
    IPlayerState playerState,
    GameData gameData,
    MarketService market,
    PopularService popular,
    PopularCache popularCache)
{
    private static readonly TimeSpan MinRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(10);

    // Adding several items in a row (or clicking Add repeatedly) makes one refresh a moment after the last add.
    private readonly RefreshDebouncer addDebouncer = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    private DateTime lastRefreshAttempt = DateTime.MinValue;
    private int analysisSignature;
    private uint popularAutoScanWorld;

    public GameData GameData => gameData;

    public MarketService Market => market;

    public PopularService Popular => popular;

    public IReadOnlyList<ItemAnalysis> Analyses { get; private set; } = [];

    /// <summary>The world the player is on right now (differs from the home world while visiting). Game thread only.</summary>
    public (uint Id, string Name)? CurrentWorld() => playerState.IsLoaded ? Named(playerState.CurrentWorld.RowId) : null;

    /// <summary>The world the player's character belongs to. Game thread only.</summary>
    public (uint Id, string Name)? HomeWorld() => playerState.IsLoaded ? Named(playerState.HomeWorld.RowId) : null;

    /// <summary>
    /// The world all prices are for ("My world"): the home world or the current world, as chosen in the settings.
    /// Its data center is the one searched for the cheapest prices. Game thread only.
    /// </summary>
    public (uint Id, string Name)? PricingWorld()
    {
        if (!playerState.IsLoaded)
            return null;

        var id = PricingWorldSelector.Select(configuration.UseHomeWorld, playerState.HomeWorld.RowId, playerState.CurrentWorld.RowId);
        return id is { } world ? Named(world) : null;
    }

    private (uint Id, string Name)? Named(uint worldId) => worldId == 0 ? null : (worldId, gameData.GetWorldName(worldId));

    public bool IsTracked(uint itemId) => configuration.TrackedItemIds.Contains(itemId);

    public void Add(uint itemId)
    {
        if (IsTracked(itemId))
            return;

        configuration.TrackedItemIds.Add(itemId);
        configuration.Save(pluginInterface);
        addDebouncer.Request(DateTimeOffset.UtcNow);
    }

    /// <summary>True while a refresh for newly added items is waiting for the burst of adds to end.</summary>
    public bool IsRefreshPending => addDebouncer.IsPending;

    /// <summary>Called every game frame, also while the window is closed, so a refresh after adding an item still happens.</summary>
    public void Update()
    {
        if (addDebouncer.TryTake(DateTimeOffset.UtcNow))
            Refresh();
    }

    public void Remove(uint itemId)
    {
        if (configuration.TrackedItemIds.Remove(itemId))
            configuration.Save(pluginInterface);
    }

    /// <summary>How long the Refresh button stays disabled after a refresh, so it cannot be spammed.</summary>
    public TimeSpan RefreshCooldownRemaining
    {
        get
        {
            var remaining = ManualCooldown - (DateTime.UtcNow - lastRefreshAttempt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>How old the prices on screen are, or null when there are none for this world.</summary>
    public TimeSpan? DataAge =>
        HasDataForPricingWorld && market.LastUpdated is { } updated ? DateTimeOffset.UtcNow - updated : null;

    public Freshness Freshness => DataAge is { } age
        ? DataFreshness.Classify(age, configuration.AutoRefresh, RefreshInterval)
        : Freshness.Fresh;

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Max(1, configuration.AutoRefreshMinutes));

    /// <summary>Refreshes now. This is a manual action (button, adding an item): it ignores the failure backoff.</summary>
    public void Refresh()
    {
        if (PricingWorld() is not { } world || configuration.TrackedItemIds.Count == 0)
            return;

        lastRefreshAttempt = DateTime.UtcNow;
        market.Refresh(
            world.Name,
            gameData.GetDataCenterName(world.Id),
            world.Id,
            configuration.TrackedItemIds,
            fetchWorldListings: configuration.SellPriceBasis == SellPriceBasis.Robust);
    }

    /// <summary>Scans the sales velocity of every craftable, marketable item on the current world.</summary>
    public void ScanPopular()
    {
        if (PricingWorld() is not { } world)
            return;

        popular.Scan(world.Name, world.Id, gameData.CraftableItems.Select(i => i.Id).ToList());
    }

    /// <summary>
    /// Called every frame the Popular tab is drawn. Shows the cached scan of this world if there is one, and
    /// starts at most one automatic scan per world and session when there is none or it has expired.
    /// </summary>
    public void PopularTick()
    {
        if (PricingWorld() is not { } world || popular.IsScanning)
            return;

        if (popular.WorldId != world.Id && popularCache.Get(world.Id) is { } cached)
            popular.Restore(cached);

        var maxAge = TimeSpan.FromHours(Math.Max(1, configuration.PopularCacheHours));
        var age = popular.WorldId == world.Id && popular.LastScanned is { } scanned
            ? DateTimeOffset.UtcNow - scanned
            : TimeSpan.MaxValue;

        if (age > maxAge && popularAutoScanWorld != world.Id)
        {
            popularAutoScanWorld = world.Id;
            ScanPopular();
        }
    }

    /// <summary>Called every frame the main window is drawn: refreshes stale data and rebuilds the analysis.</summary>
    public void Tick()
    {
        // After failures the market service says when automatic refreshing may try again, and stops after too many.
        if (configuration.AutoRefresh && PricingWorld() is { } world && configuration.TrackedItemIds.Count > 0
            && !market.IsRefreshing && DateTime.UtcNow - lastRefreshAttempt > MinRetryInterval
            && !market.IsAutoRefreshBlocked(DateTimeOffset.UtcNow))
        {
            var age = market.LastUpdated is { } updated ? DateTimeOffset.UtcNow - updated : TimeSpan.MaxValue;
            if (market.WorldId != world.Id || age > RefreshInterval)
                Refresh();
        }

        RebuildAnalysesIfNeeded();
    }

    /// <summary>True when the loaded market data belongs to the pricing world.</summary>
    public bool HasDataForPricingWorld => PricingWorld() is { } world && market.LastUpdated is not null && market.WorldId == world.Id;

    private void RebuildAnalysesIfNeeded()
    {
        // Prices from another world are meaningless here, so they are hidden rather than shown as if current.
        var dataIsCurrent = HasDataForPricingWorld;

        var hash = new HashCode();
        hash.Add(market.Version);
        hash.Add(dataIsCurrent);
        hash.Add(configuration.PricingSettings);
        foreach (var id in configuration.TrackedItemIds)
            hash.Add(id);

        var signature = hash.ToHashCode();
        if (signature == analysisSignature)
            return;

        analysisSignature = signature;

        var data = market.Data;
        var dcListings = market.DcListings;
        var worldListings = market.WorldListings;
        var calculator = new ProfitCalculator(
            gameData,
            id => dataIsCurrent && data.TryGetValue(id, out var m) ? m : null,
            configuration.PricingSettings,
            market.WorldId,
            id => dcListings.GetValueOrDefault(id),
            id => worldListings.GetValueOrDefault(id));

        Analyses = configuration.TrackedItemIds.Select(calculator.Analyze).ToList();
    }
}
