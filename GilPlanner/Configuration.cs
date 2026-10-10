using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using GilPlanner.Pricing;

namespace GilPlanner;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Open the window every time the game starts (off by default, `/gilplanner` opens it).</summary>
    public bool OpenOnStartup { get; set; } = false;

    // Gil planner
    public List<uint> TrackedItemIds { get; set; } = [];

    public bool AutoRefresh { get; set; } = true;

    public int AutoRefreshMinutes { get; set; } = 5;

    public bool PreferHq { get; set; } = true;

    public float MarketTaxPercent { get; set; } = 5f;

    public SellPriceBasis SellPriceBasis { get; set; } = SellPriceBasis.Robust;

    /// <summary>How many finished items material costs are planned for (buying more costs more per unit).</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>The share of the sales on your world you expect to get; used for the profit per day estimate.</summary>
    public int MarketSharePercent { get; set; } = 25;

    /// <summary>How long a Popular scan is shown from the cache before a new scan starts.</summary>
    public int PopularCacheHours { get; set; } = 12;

    public PricingSettings PricingSettings => new(PreferHq, MarketTaxPercent, SellPriceBasis, BatchSize, MarketSharePercent);

    public void Save(IDalamudPluginInterface pluginInterface) => pluginInterface.SavePluginConfig(this);
}
