using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class PopularityRankerTests
{
    private static QualityData Quality(long? min = null, long? avg = null, double velocity = 0) =>
        new(min, null, null, avg, velocity, velocity);

    private static ItemMarket Market(uint id, QualityData? nq = null, QualityData? hq = null) =>
        new(id, nq ?? QualityData.Empty, hq ?? QualityData.Empty, null);

    [Fact]
    public void Ranks_by_units_sold_per_day_and_drops_slow_sellers()
    {
        var ranked = PopularityRanker.Rank(
        [
            Market(1, Quality(100, 100, 5)),
            Market(2, Quality(100, 100, 50)),
            Market(3, Quality(100, 100, 0.4)),
        ]);

        Assert.Equal([2u, 1u], ranked.Select(p => p.ItemId));
    }

    [Fact]
    public void Velocity_adds_both_qualities_and_price_follows_the_quality_that_sells_more()
    {
        var item = PopularityRanker.Rank([Market(1, Quality(100, 90, 3), Quality(500, 450, 7))]).Single();

        Assert.Equal(10, item.Velocity, 6);
        Assert.True(item.IsHq);
        Assert.Equal(500, item.Price);
        Assert.Equal(3 * 90 + 7 * 450, item.GilPerDay, 6);
    }

    [Fact]
    public void Gil_per_day_falls_back_to_lowest_listing_without_sale_average_and_price_may_be_missing()
    {
        var withListing = PopularityRanker.Rank([Market(1, Quality(min: 200, velocity: 2))]).Single();
        Assert.Equal(400, withListing.GilPerDay, 6);

        var noPrice = PopularityRanker.Rank([Market(2, Quality(velocity: 2))]).Single();
        Assert.Null(noPrice.Price);
        Assert.Equal(0, noPrice.GilPerDay, 6);
    }

    [Fact]
    public void Take_limits_the_result()
    {
        var markets = Enumerable.Range(1, 10).Select(i => Market((uint)i, Quality(10, 10, i + 1)));

        var ranked = PopularityRanker.Rank(markets, take: 3);

        Assert.Equal([10u, 9u, 8u], ranked.Select(p => p.ItemId));
    }
}
