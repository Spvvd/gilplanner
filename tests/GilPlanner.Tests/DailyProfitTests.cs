using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class DailyProfitTests
{
    private const uint MyWorld = 10;
    private const uint Zodiark = 20;
    private const uint Product = 1, Mat = 2;

    private sealed class Data : IRecipeSource
    {
        public Dictionary<uint, List<Recipe>> Recipes { get; } = new();
        public HashSet<uint> Gatherable { get; } = new();

        public IReadOnlyList<Recipe> GetRecipes(uint itemId) => Recipes.GetValueOrDefault(itemId) ?? [];
        public bool IsGatherable(uint itemId) => Gatherable.Contains(itemId);
        public long? GetVendorPrice(uint itemId) => null;
        public bool CanBeHq(uint itemId) => false;
        public string GetItemName(uint itemId) => "x";
    }

    private static ItemAnalysis Analyze(
        long sell, double velocity, long? dcPrice = null, uint? dcWorld = null, long? matPrice = null, bool gatherable = false,
        double share = 100, int batch = 1, Dictionary<uint, DateTimeOffset>? uploads = null)
    {
        var data = new Data();
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = new ItemMarket(Product, new QualityData(sell, dcPrice, dcWorld, null, velocity, velocity), QualityData.Empty, null, uploads),
        };

        if (matPrice is { } price)
        {
            data.Recipes[Product] = [new Recipe(1, Product, 1, [new Ingredient(Mat, 1)])];
            market[Mat] = new ItemMarket(Mat, new QualityData(null, price, Zodiark, null, 0, 0), QualityData.Empty, null);
            if (gatherable)
                data.Gatherable.Add(Mat);
        }

        return new ProfitCalculator(
                data,
                id => market.GetValueOrDefault(id),
                new PricingSettings(false, 0, SellPriceBasis.LowestListing, batch, share),
                MyWorld)
            .Analyze(Product);
    }

    [Fact]
    public void Daily_profit_is_best_profit_times_sales_per_day_times_my_share()
    {
        // craft profit 1000 - 400 = 600, gather 1000 (free material), flip 1000 - 900 = 100
        var a = Analyze(1000, velocity: 40, dcPrice: 900, dcWorld: Zodiark, matPrice: 400, gatherable: true, share: 25);

        Assert.Equal(Strategy.Gather, a.BestStrategy);
        Assert.Equal(1000 * 40 * 0.25, a.DailyProfit!.Value, 6);
    }

    [Fact]
    public void Picks_the_strategy_with_the_highest_profit_per_item()
    {
        var craftBest = Analyze(1000, 10, dcPrice: 950, dcWorld: Zodiark, matPrice: 200);
        Assert.Equal(Strategy.Craft, craftBest.BestStrategy); // craft 800 vs flip 50

        var flipBest = Analyze(1000, 10, dcPrice: 300, dcWorld: Zodiark, matPrice: 900);
        Assert.Equal(Strategy.Flip, flipBest.BestStrategy); // flip 700 vs craft 100
    }

    [Fact]
    public void A_slow_seller_with_a_big_profit_loses_against_a_fast_seller_with_a_small_one()
    {
        var slow = Analyze(11000, velocity: 0.3, dcPrice: 1000, dcWorld: Zodiark, share: 25);
        var fast = Analyze(1800, velocity: 200, dcPrice: 1000, dcWorld: Zodiark, share: 25);

        Assert.Equal(10000 * 0.3 * 0.25, slow.DailyProfit!.Value, 6);
        Assert.Equal(800 * 200 * 0.25, fast.DailyProfit!.Value, 6);
        Assert.True(fast.DailyProfit > slow.DailyProfit);
    }

    [Fact]
    public void Days_to_sell_the_batch_use_my_share_of_the_sales()
    {
        var a = Analyze(1000, velocity: 8, dcPrice: 500, dcWorld: Zodiark, share: 25, batch: 10);

        Assert.Equal(10 / (8 * 0.25), a.DaysToSellBatch!.Value, 6); // 5 days
    }

    [Fact]
    public void Without_sales_there_is_no_estimate_of_days_and_the_daily_profit_is_zero()
    {
        var a = Analyze(1000, velocity: 0, dcPrice: 500, dcWorld: Zodiark);

        Assert.Null(a.DaysToSellBatch);
        Assert.Equal(0, a.DailyProfit!.Value, 6);
    }

    [Fact]
    public void Without_any_priced_strategy_there_is_no_daily_profit()
    {
        var a = Analyze(1000, velocity: 5); // no DC price, no recipe

        Assert.Null(a.BestStrategy);
        Assert.Null(a.DailyProfit);
    }

    [Fact]
    public void A_losing_best_strategy_gives_a_negative_daily_profit()
    {
        var a = Analyze(100, velocity: 10, dcPrice: 500, dcWorld: Zodiark, share: 50);

        Assert.True(a.DailyProfit < 0);
        Assert.Equal((100 - 500) * 10 * 0.5, a.DailyProfit!.Value, 6);
    }

    [Fact]
    public void Share_is_clamped_to_zero_to_one_hundred_percent()
    {
        Assert.Equal(1000 * 10 * 1.0, Analyze(1000, 10, dcPrice: 0, dcWorld: Zodiark, share: 400).DailyProfit!.Value, 6);
        Assert.Equal(0, Analyze(1000, 10, dcPrice: 0, dcWorld: Zodiark, share: -5).DailyProfit!.Value, 6);
    }

    [Fact]
    public void The_upload_time_of_the_cheapest_dc_world_is_reported()
    {
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(at, Analyze(1000, 5, dcPrice: 500, dcWorld: Zodiark, uploads: new() { [Zodiark] = at }).DcWorldUploadTime);
        Assert.Null(Analyze(1000, 5, dcPrice: 500, dcWorld: Zodiark, uploads: new() { [MyWorld] = at }).DcWorldUploadTime);
        Assert.Null(Analyze(1000, 5, dcPrice: 500, dcWorld: Zodiark).DcWorldUploadTime);
    }
}

public class UploadAgeTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    [Fact]
    public void Parser_keeps_the_upload_time_of_every_reported_world()
    {
        // Real response saved on 2026-10-09: world 66 (own) and 71 (cheapest in the region) are reported.
        var item = UniversalisParser.Parse(Read("aggregated-sample.json"), 66)[36113];

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791489808413), item.WorldUploadTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791562767808), item.UploadTimes![71]);
        Assert.Equal(2, item.UploadTimes.Count);
    }

    [Theory]
    [InlineData(0, Freshness.Fresh)]
    [InlineData(6, Freshness.Fresh)]
    [InlineData(7, Freshness.Stale)]
    [InlineData(48, Freshness.Stale)]
    [InlineData(49, Freshness.VeryStale)]
    [InlineData(24 * 30, Freshness.VeryStale)]
    public void Upload_age_is_fine_for_six_hours_and_a_ghost_price_after_two_days(double hours, Freshness expected) =>
        Assert.Equal(expected, DataFreshness.ClassifyUpload(TimeSpan.FromHours(hours)));
}

public class RefreshDebouncerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static RefreshDebouncer Create() => new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    [Fact]
    public void Does_nothing_without_a_request()
    {
        var debouncer = Create();

        Assert.False(debouncer.IsPending);
        Assert.False(debouncer.TryTake(T0.AddHours(1)));
    }

    [Fact]
    public void A_single_request_fires_after_the_quiet_period_exactly_once()
    {
        var debouncer = Create();
        debouncer.Request(T0);

        Assert.True(debouncer.IsPending);
        Assert.False(debouncer.TryTake(T0.AddSeconds(1.9)));
        Assert.True(debouncer.TryTake(T0.AddSeconds(2)));
        Assert.False(debouncer.TryTake(T0.AddSeconds(3)));
        Assert.False(debouncer.IsPending);
    }

    [Fact]
    public void A_burst_of_requests_becomes_one_refresh()
    {
        var debouncer = Create();
        for (var i = 0; i < 5; i++)
            debouncer.Request(T0.AddSeconds(i)); // one per second

        Assert.False(debouncer.TryTake(T0.AddSeconds(5.9))); // 1.9 s after the last request
        Assert.True(debouncer.TryTake(T0.AddSeconds(6)));
        Assert.False(debouncer.TryTake(T0.AddSeconds(20)));
    }

    [Fact]
    public void Endless_requests_cannot_postpone_the_refresh_beyond_the_maximum_wait()
    {
        var debouncer = Create();
        for (var i = 0; i < 10; i++)
            debouncer.Request(T0.AddSeconds(i)); // a request every second, never quiet for 2 s

        Assert.True(debouncer.TryTake(T0.AddSeconds(10)));
    }

    [Fact]
    public void A_new_burst_after_firing_starts_over()
    {
        var debouncer = Create();
        debouncer.Request(T0);
        Assert.True(debouncer.TryTake(T0.AddSeconds(2)));

        debouncer.Request(T0.AddSeconds(30));

        Assert.False(debouncer.TryTake(T0.AddSeconds(31)));
        Assert.True(debouncer.TryTake(T0.AddSeconds(32)));
    }
}
