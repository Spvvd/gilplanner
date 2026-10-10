using System;
using System.Collections.Generic;

namespace GilPlanner.Pricing;

public sealed record Ingredient(uint ItemId, int Amount);

/// <summary>One crafting recipe: <see cref="Yield"/> items of <see cref="ResultItemId"/> per craft.</summary>
public sealed record Recipe(uint RecipeId, uint ResultItemId, int Yield, IReadOnlyList<Ingredient> Ingredients);

/// <summary>One market board listing (a stack of <see cref="Quantity"/> items at <see cref="PricePerUnit"/> each).</summary>
public sealed record Listing(long PricePerUnit, int Quantity, uint WorldId, bool Hq);

/// <summary>Current listings of one item, cheapest first.</summary>
public sealed record ItemListings(uint ItemId, IReadOnlyList<Listing> Listings);

/// <summary>Part of a shopping plan: buy <see cref="Quantity"/> units on <see cref="WorldId"/> at <see cref="PricePerUnit"/>.</summary>
public sealed record Purchase(uint WorldId, int Quantity, long PricePerUnit);

/// <summary>Market numbers for one item quality (NQ or HQ). Everything is null/0 when Universalis has no data.</summary>
public sealed record QualityData(
    long? WorldMin,
    long? DcMin,
    uint? DcMinWorldId,
    long? WorldAvg,
    double WorldVelocity,
    double DcVelocity)
{
    public static readonly QualityData Empty = new(null, null, null, null, 0, 0);
}

/// <param name="WorldUploadTime">When the market board of your own world was last uploaded to Universalis for this item.</param>
/// <param name="UploadTimes">The same for every world Universalis reported for this item (your world, the cheapest DC world, ...).</param>
public sealed record ItemMarket(
    uint ItemId,
    QualityData Nq,
    QualityData Hq,
    DateTimeOffset? WorldUploadTime,
    IReadOnlyDictionary<uint, DateTimeOffset>? UploadTimes = null);

// Values are saved in the plugin config as numbers, so new members must be appended.
public enum SellPriceBasis
{
    LowestListing,
    AverageSale,

    /// <summary>Median of the cheapest few listings on your world, so a single lowball does not set the price.</summary>
    Robust,
}

/// <param name="BatchSize">How many finished items the material costs are planned for (buying more costs more per unit).</param>
/// <param name="MarketSharePercent">
/// The share of the sales on your world you expect to get. Other sellers compete for the same buyers, so you will not
/// sell everything that sells.
/// </param>
public readonly record struct PricingSettings(
    bool PreferHq,
    double MarketTaxPercent,
    SellPriceBasis Basis,
    int BatchSize = 1,
    double MarketSharePercent = 25);

public enum Strategy
{
    Flip,
    Craft,
    Gather,
}

public enum CostSource
{
    Market,
    Vendor,
    Gathered,
    Crafted,
    Unavailable,
}

/// <summary>
/// One material of a batch. <see cref="TotalUnits"/> and <see cref="TotalCost"/> cover the whole batch;
/// <see cref="Purchases"/> says where to buy when <see cref="Source"/> is <see cref="CostSource.Market"/>.
/// </summary>
public sealed record CostLine(
    uint ItemId,
    string Name,
    int UnitsPerCraft,
    int TotalUnits,
    double TotalCost,
    CostSource Source,
    uint? WorldId,
    IReadOnlyList<Purchase> Purchases,
    int ShortfallUnits)
{
    public double UnitCost => TotalUnits > 0 ? TotalCost / TotalUnits : 0;
}

/// <summary>Profit per single item. <see cref="Percent"/> is null when the cost is zero.</summary>
public sealed record ProfitResult(double Gil, double? Percent, double Cost);

/// <param name="WorldPrice">The price on your world that profits are based on (before market tax).</param>
/// <param name="DcWorldUploadTime">When the world with the cheapest DC listing was last uploaded for this item.</param>
/// <param name="BestStrategy">The strategy with the highest profit per item among those that could be priced.</param>
/// <param name="DailyProfit">
/// Estimated profit per day: best profit per item x sales per day x your expected market share. Null without a
/// priced strategy.
/// </param>
/// <param name="DaysToSellBatch">How long selling a whole batch takes at your expected share of the sales (null without sales).</param>
/// <param name="PriceDeviationPercent">How far <paramref name="WorldPrice"/> is above (+) or below (-) the recent average sale price.</param>
public sealed record ItemAnalysis(
    uint ItemId,
    bool HasData,
    bool IsHq,
    long? WorldPrice,
    long? LowestListing,
    long? AverageSale,
    double? PriceDeviationPercent,
    long? DcPrice,
    uint? DcWorldId,
    double Velocity,
    DateTimeOffset? UploadTime,
    ProfitResult? Flip,
    ProfitResult? Craft,
    ProfitResult? Gather,
    IReadOnlyList<CostLine> CraftLines,
    IReadOnlyList<CostLine> GatherLines,
    int BatchSize,
    DateTimeOffset? DcWorldUploadTime = null,
    Strategy? BestStrategy = null,
    double? DailyProfit = null,
    double? DaysToSellBatch = null);

/// <summary>Static game data the calculator needs; implemented on top of Lumina in the plugin.</summary>
public interface IRecipeSource
{
    IReadOnlyList<Recipe> GetRecipes(uint itemId);
    bool IsGatherable(uint itemId);
    long? GetVendorPrice(uint itemId);
    bool CanBeHq(uint itemId);
    string GetItemName(uint itemId);
}
