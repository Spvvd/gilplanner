using System;
using System.Collections.Generic;
using System.Linq;

namespace GilPlanner.Pricing;

/// <summary>
/// Works out flip / craft / gather profit per item. Prices are per single item; "cost" is what it takes to
/// obtain one item, "profit" is the sell price (after market tax) minus that cost.
/// </summary>
/// <remarks>
/// Materials are costed for a whole batch (<see cref="PricingSettings.BatchSize"/> items): when listings are
/// available the cheapest listings are bought one after another, so large quantities cost more per unit.
/// Without listings the aggregated lowest price is used for every unit.
/// </remarks>
public sealed class ProfitCalculator(
    IRecipeSource recipes,
    Func<uint, ItemMarket?> market,
    PricingSettings settings,
    uint worldId,
    Func<uint, ItemListings?>? dcListings = null,
    Func<uint, ItemListings?>? worldListings = null)
{
    /// <summary>How many levels of intermediate crafts are considered when costing materials.</summary>
    public const int MaxCraftDepth = 3;

    /// <summary>How many of the cheapest listings the robust sell price looks at.</summary>
    public const int RobustSampleSize = 5;

    private enum Mode
    {
        Craft,
        Gather,
    }

    private sealed record Option(double TotalCost, CostSource Source, IReadOnlyList<Purchase> Purchases, int Shortfall);

    private sealed record Plan(IReadOnlyList<CostLine> Lines, double? TotalCost, int ItemsProduced)
    {
        public double? CostPerItem => TotalCost / ItemsProduced;
    }

    public ItemAnalysis Analyze(uint itemId)
    {
        var batch = Math.Max(1, settings.BatchSize);
        var data = market(itemId);
        if (data is null)
            return new ItemAnalysis(itemId, false, false, null, null, null, null, null, null, 0, null, null, null, null, [], [], batch);

        var useHq = settings.PreferHq && recipes.CanBeHq(itemId) && HasPrice(data.Hq);
        var quote = useHq ? data.Hq : data.Nq;
        var sellPrice = SellPrice(itemId, quote, useHq);
        var net = sellPrice is { } gross ? gross * (1 - settings.MarketTaxPercent / 100.0) : (double?)null;

        ProfitResult? flip = null;
        if (net is { } && quote.DcMin is { } dcPrice && quote.DcMinWorldId != worldId)
            flip = Profit(net.Value, dcPrice);

        var craftPlan = BestPlan(itemId, Mode.Craft, batch, 0, []);
        var gatherPlan = BestPlan(itemId, Mode.Gather, batch, 0, []);

        double? deviation = quote.WorldAvg is > 0 && sellPrice is { } sell
            ? (sell / quote.WorldAvg.Value - 1) * 100
            : null;

        var craft = net is { } && craftPlan?.CostPerItem is { } craftCost ? Profit(net.Value, craftCost) : null;
        var gather = net is { } && gatherPlan?.CostPerItem is { } gatherCost ? Profit(net.Value, gatherCost) : null;
        var (best, dailyProfit, daysToSell) = Daily(flip, craft, gather, quote.WorldVelocity, batch);

        DateTimeOffset? dcWorldUpload = quote.DcMinWorldId is { } dcWorldId && data.UploadTimes?.TryGetValue(dcWorldId, out var uploaded) == true
            ? uploaded
            : null;

        return new ItemAnalysis(
            itemId,
            true,
            useHq,
            sellPrice is { } p ? (long)Math.Round(p) : null,
            quote.WorldMin,
            quote.WorldAvg,
            deviation,
            quote.DcMin,
            quote.DcMinWorldId,
            quote.WorldVelocity,
            data.WorldUploadTime,
            flip,
            craft,
            gather,
            craftPlan?.Lines ?? [],
            gatherPlan?.Lines ?? [],
            batch,
            dcWorldUpload,
            best,
            dailyProfit,
            daysToSell);
    }

    /// <summary>
    /// What the best strategy could earn per day. Only a share of the sales on your world is yours, so the daily
    /// profit is the profit per item times sales per day times that share.
    /// </summary>
    private (Strategy? Best, double? DailyProfit, double? DaysToSell) Daily(
        ProfitResult? flip, ProfitResult? craft, ProfitResult? gather, double velocity, int batch)
    {
        (Strategy Strategy, ProfitResult Profit)? best = null;
        foreach (var (strategy, profit) in new[] { (Strategy.Craft, craft), (Strategy.Gather, gather), (Strategy.Flip, flip) })
        {
            if (profit is not null && (best is null || profit.Gil > best.Value.Profit.Gil))
                best = (strategy, profit);
        }

        var share = Math.Clamp(settings.MarketSharePercent, 0, 100) / 100.0;
        var mySalesPerDay = velocity * share;
        double? daysToSell = mySalesPerDay > 0 ? batch / mySalesPerDay : null;

        return best is { } b ? (b.Strategy, b.Profit.Gil * mySalesPerDay, daysToSell) : (null, null, daysToSell);
    }

    private static bool HasPrice(QualityData data) => data.WorldMin is not null || data.WorldAvg is not null;

    private double? SellPrice(uint itemId, QualityData quote, bool hq) => settings.Basis switch
    {
        SellPriceBasis.AverageSale => quote.WorldAvg ?? quote.WorldMin,
        SellPriceBasis.Robust => RobustPrice(itemId, hq) ?? quote.WorldMin ?? quote.WorldAvg,
        _ => quote.WorldMin ?? quote.WorldAvg,
    };

    /// <summary>
    /// Median of the cheapest listings of the sold quality. With fewer than three listings there is nothing to
    /// cross-check against, so the lowest one is returned.
    /// </summary>
    private double? RobustPrice(uint itemId, bool hq)
    {
        if (worldListings?.Invoke(itemId) is not { } listings)
            return null;

        var prices = listings.Listings
            .Where(l => l.Hq == hq)
            .Select(l => l.PricePerUnit)
            .Order()
            .Take(RobustSampleSize)
            .ToList();

        return prices.Count switch
        {
            0 => null,
            < 3 => prices[0],
            _ => prices.Count % 2 == 1
                ? prices[prices.Count / 2]
                : (prices[prices.Count / 2 - 1] + prices[prices.Count / 2]) / 2.0,
        };
    }

    private static ProfitResult Profit(double net, double cost) =>
        new(net - cost, cost > 0 ? (net - cost) / cost * 100.0 : null, cost);

    /// <summary>The cheapest way to make at least <paramref name="units"/> of <paramref name="itemId"/>.</summary>
    private Plan? BestPlan(uint itemId, Mode mode, long units, int depth, HashSet<uint> visiting)
    {
        var options = recipes.GetRecipes(itemId);
        if (options.Count == 0)
            return null;

        visiting.Add(itemId);
        try
        {
            Plan? best = null;
            foreach (var recipe in options)
            {
                var yield = Math.Max(1, recipe.Yield);
                var crafts = (long)Math.Ceiling(units / (double)yield);
                var lines = new List<CostLine>(recipe.Ingredients.Count);
                double total = 0;
                var complete = true;

                foreach (var ingredient in recipe.Ingredients)
                {
                    var needed = ingredient.Amount * crafts;
                    var option = Resolve(ingredient.ItemId, needed, mode, depth, visiting);
                    lines.Add(new CostLine(
                        ingredient.ItemId,
                        recipes.GetItemName(ingredient.ItemId),
                        ingredient.Amount,
                        (int)needed,
                        option?.TotalCost ?? 0,
                        option?.Source ?? CostSource.Unavailable,
                        option?.Purchases.FirstOrDefault()?.WorldId,
                        option?.Purchases ?? [],
                        option?.Shortfall ?? 0));

                    if (option is null)
                        complete = false;
                    else
                        total += option.TotalCost;
                }

                var plan = new Plan(lines, complete ? total : null, (int)(crafts * yield));
                if (best is null || IsBetter(plan, best))
                    best = plan;
            }

            return best;
        }
        finally
        {
            visiting.Remove(itemId);
        }
    }

    private static bool IsBetter(Plan candidate, Plan current) =>
        candidate.CostPerItem is { } c && (current.CostPerItem is not { } cur || c < cur);

    /// <summary>The cheapest way to get <paramref name="units"/> of a material, or null when no price is known.</summary>
    private Option? Resolve(uint itemId, long units, Mode mode, int depth, HashSet<uint> visiting)
    {
        var candidates = new List<Option>(4);

        if (mode == Mode.Gather && recipes.IsGatherable(itemId))
            candidates.Add(new Option(0, CostSource.Gathered, [], 0));

        if (recipes.GetVendorPrice(itemId) is { } vendor)
            candidates.Add(new Option(vendor * (double)units, CostSource.Vendor, [], 0));

        if (BuyFromMarket(itemId, units) is { } buy)
            candidates.Add(buy);

        if (depth < MaxCraftDepth && !visiting.Contains(itemId)
            && BestPlan(itemId, mode, units, depth + 1, visiting)?.TotalCost is { } craftCost)
            candidates.Add(new Option(craftCost, CostSource.Crafted, [], 0));

        // Stable sort keeps the declared preference on ties: gathered, vendor, market, crafted.
        return candidates.Count == 0 ? null : candidates.OrderBy(c => c.TotalCost).First();
    }

    private Option? BuyFromMarket(uint itemId, long units)
    {
        if (dcListings?.Invoke(itemId) is { Listings.Count: > 0 } listings)
            return WalkListings(listings.Listings, units);

        // No listings loaded: price every unit at the cheapest DC listing.
        if (market(itemId) is { } data)
        {
            var cheapest = new[] { data.Nq, data.Hq }
                .Where(q => q.DcMin is not null)
                .OrderBy(q => q.DcMin)
                .FirstOrDefault();
            if (cheapest?.DcMin is { } price)
                return new Option(price * (double)units, CostSource.Market, [new Purchase(cheapest.DcMinWorldId ?? 0, (int)units, price)], 0);
        }

        return null;
    }

    /// <summary>Buys the cheapest listings until <paramref name="units"/> are covered; any rest is priced like the last listing.</summary>
    private static Option WalkListings(IReadOnlyList<Listing> listings, long units)
    {
        var purchases = new List<Purchase>();
        var remaining = units;
        double total = 0;

        foreach (var listing in listings)
        {
            if (remaining <= 0)
                break;

            var take = (int)Math.Min(remaining, listing.Quantity);
            if (take <= 0)
                continue;

            total += take * (double)listing.PricePerUnit;
            remaining -= take;

            if (purchases.Count > 0 && purchases[^1] is { } last && last.WorldId == listing.WorldId && last.PricePerUnit == listing.PricePerUnit)
                purchases[^1] = last with { Quantity = last.Quantity + take };
            else
                purchases.Add(new Purchase(listing.WorldId, take, listing.PricePerUnit));
        }

        if (remaining > 0)
            total += remaining * (double)listings[^1].PricePerUnit;

        return new Option(total, CostSource.Market, purchases, (int)remaining);
    }
}
