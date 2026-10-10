using System.Collections.Generic;
using System.Linq;

namespace GilPlanner.Pricing;

/// <param name="Price">Current price on your world for the quality that sells more (null without listings).</param>
/// <param name="Velocity">Units sold per day on your world, NQ + HQ.</param>
/// <param name="GilPerDay">Estimated daily turnover on your world: sales per day x average sale price.</param>
public sealed record PopularItem(uint ItemId, long? Price, bool IsHq, double Velocity, double GilPerDay);

public static class PopularityRanker
{
    /// <summary>The best-selling items on your world, most units sold per day first.</summary>
    public static List<PopularItem> Rank(IEnumerable<ItemMarket> markets, double minVelocity = 1, int take = 200) =>
        markets
            .Select(ToPopularItem)
            .Where(p => p.Velocity >= minVelocity)
            .OrderByDescending(p => p.Velocity)
            .Take(take)
            .ToList();

    private static PopularItem ToPopularItem(ItemMarket market)
    {
        var dominant = market.Hq.WorldVelocity > market.Nq.WorldVelocity ? market.Hq : market.Nq;
        return new PopularItem(
            market.ItemId,
            dominant.WorldMin ?? dominant.WorldAvg,
            ReferenceEquals(dominant, market.Hq),
            market.Nq.WorldVelocity + market.Hq.WorldVelocity,
            Turnover(market.Nq) + Turnover(market.Hq));
    }

    private static double Turnover(QualityData data) => data.WorldVelocity * (data.WorldAvg ?? data.WorldMin ?? 0);
}
