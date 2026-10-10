using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class ListingsParserTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    [Fact]
    public void Parses_multi_item_response_cheapest_first()
    {
        // Real response of /api/v2/Light/5333,36113?listings=4&... saved on 2026-10-10.
        var result = UniversalisParser.ParseListings(Read("listings-multi.json"));

        Assert.Equal(2, result.Count);
        var listings = result[36113].Listings;
        Assert.Equal(4, listings.Count);
        Assert.Equal(495, listings[0].PricePerUnit);
        Assert.False(listings[0].Hq);
        Assert.Equal(66u, listings[0].WorldId);
        Assert.True(listings[1].Hq);
        Assert.Equal(99, listings[1].Quantity);
        Assert.Equal(402u, listings[3].WorldId);
    }

    [Fact]
    public void Parses_single_item_response_where_world_ids_are_missing()
    {
        // Real response of /api/v2/Odin/5333?listings=4&... (a world request has no worldID per listing).
        var result = UniversalisParser.ParseListings(Read("listings-single.json"));

        var listings = result[5333].Listings;
        Assert.Equal([50L, 55, 99, 100], listings.Select(l => l.PricePerUnit));
        Assert.All(listings, l => Assert.Equal(0u, l.WorldId));
    }

    [Fact]
    public void Skips_mannequin_listings_and_sorts_by_price()
    {
        const string json = """
            {"itemID":7,"listings":[
              {"pricePerUnit":900,"quantity":1,"worldID":1,"hq":false,"onMannequin":false},
              {"pricePerUnit":5,"quantity":1,"worldID":1,"hq":false,"onMannequin":true},
              {"pricePerUnit":100,"quantity":2,"worldID":2,"hq":true,"onMannequin":false}]}
            """;

        var listings = UniversalisParser.ParseListings(json)[7].Listings;

        Assert.Equal([100L, 900], listings.Select(l => l.PricePerUnit));
    }

    [Fact]
    public void Items_without_listings_are_kept_empty()
    {
        var result = UniversalisParser.ParseListings("""{"items":{"9":{"itemID":9,"listings":[]}}}""");

        Assert.Empty(result[9].Listings);
    }
}

public class QuantityAwareCostTests
{
    private const uint MyWorld = 10;
    private const uint Zodiark = 20;
    private const uint Odin = 30;
    private const uint Product = 1, Mat = 2, Mid = 3, Leaf = 4;

    private sealed class Data : IRecipeSource
    {
        public Dictionary<uint, List<Recipe>> Recipes { get; } = new();
        public HashSet<uint> Gatherable { get; } = new();

        public IReadOnlyList<Recipe> GetRecipes(uint itemId) => Recipes.GetValueOrDefault(itemId) ?? [];
        public bool IsGatherable(uint itemId) => Gatherable.Contains(itemId);
        public long? GetVendorPrice(uint itemId) => null;
        public bool CanBeHq(uint itemId) => true;
        public string GetItemName(uint itemId) => $"item{itemId}";

        public void AddRecipe(uint result, int yield, params (uint Id, int Amount)[] ingredients)
        {
            if (!Recipes.TryGetValue(result, out var list))
                Recipes[result] = list = [];
            list.Add(new Recipe(result * 100, result, yield, ingredients.Select(i => new Ingredient(i.Id, i.Amount)).ToList()));
        }
    }

    private static ItemListings Listings(uint id, params (long Price, int Qty, uint World, bool Hq)[] entries) =>
        new(id, entries.Select(e => new Listing(e.Price, e.Qty, e.World, e.Hq)).OrderBy(l => l.PricePerUnit).ToList());

    private static ItemMarket Market(uint id, long? world = null, long? dc = null, uint? dcWorld = null, long? avg = null) =>
        new(id, new QualityData(world, dc, dcWorld, avg, 5, 5), QualityData.Empty, null);

    private static ProfitCalculator Calc(
        Data data,
        Dictionary<uint, ItemMarket> market,
        Dictionary<uint, ItemListings>? dc = null,
        Dictionary<uint, ItemListings>? world = null,
        int batch = 1,
        SellPriceBasis basis = SellPriceBasis.LowestListing) =>
        new(
            data,
            id => market.GetValueOrDefault(id),
            new PricingSettings(false, 0, basis, batch),
            MyWorld,
            id => dc?.GetValueOrDefault(id),
            id => world?.GetValueOrDefault(id));

    [Fact]
    public void Buying_many_units_walks_up_the_listings_and_reports_where_to_buy()
    {
        var data = new Data();
        data.AddRecipe(Product, 1, (Mat, 4));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mat] = Market(Mat, dc: 10, dcWorld: Zodiark),
        };
        var dc = new Dictionary<uint, ItemListings>
        {
            [Mat] = Listings(Mat, (10, 12, Zodiark, false), (15, 20, Odin, false), (50, 99, Odin, false)),
        };

        // 10 items need 40 units: 12 @ 10 + 20 @ 15 + 8 @ 50 = 120 + 300 + 400
        var analysis = Calc(data, market, dc, batch: 10).Analyze(Product);
        var line = analysis.CraftLines.Single();

        Assert.Equal(40, line.TotalUnits);
        Assert.Equal(820, line.TotalCost, 6);
        Assert.Equal(82, analysis.Craft!.Cost, 6); // per item
        Assert.Equal(0, line.ShortfallUnits);
        Assert.Equal(
            [new Purchase(Zodiark, 12, 10), new Purchase(Odin, 20, 15), new Purchase(Odin, 8, 50)],
            line.Purchases);
        Assert.Equal(Zodiark, line.WorldId);
    }

    [Fact]
    public void Single_unit_batches_match_the_plain_lowest_price()
    {
        var data = new Data();
        data.AddRecipe(Product, 1, (Mat, 1));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mat] = Market(Mat, dc: 10, dcWorld: Zodiark),
        };
        var dc = new Dictionary<uint, ItemListings> { [Mat] = Listings(Mat, (10, 1, Zodiark, false), (99, 99, Odin, false)) };

        Assert.Equal(10, Calc(data, market, dc).Analyze(Product).Craft!.Cost, 6);
    }

    [Fact]
    public void Running_out_of_listings_prices_the_rest_like_the_last_listing_and_reports_the_shortfall()
    {
        var data = new Data();
        data.AddRecipe(Product, 1, (Mat, 5));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mat] = Market(Mat, dc: 10, dcWorld: Zodiark),
        };
        var dc = new Dictionary<uint, ItemListings> { [Mat] = Listings(Mat, (10, 4, Zodiark, false), (20, 6, Odin, false)) };

        var line = Calc(data, market, dc, batch: 4).Analyze(Product).CraftLines.Single(); // needs 20, only 10 listed

        Assert.Equal(4 * 10 + 6 * 20 + 10 * 20, line.TotalCost, 6);
        Assert.Equal(10, line.ShortfallUnits);
    }

    [Fact]
    public void Without_listings_every_unit_uses_the_aggregated_lowest_price()
    {
        var data = new Data();
        data.AddRecipe(Product, 1, (Mat, 3));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mat] = Market(Mat, dc: 10, dcWorld: Zodiark),
        };

        var analysis = Calc(data, market, batch: 5).Analyze(Product);

        Assert.Equal(30, analysis.Craft!.Cost, 6);
        Assert.Equal(15, analysis.CraftLines.Single().TotalUnits);
    }

    [Fact]
    public void Batch_size_rounds_crafts_up_and_divides_by_everything_produced()
    {
        var data = new Data();
        data.AddRecipe(Product, 3, (Mat, 2)); // 3 items per craft
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mat] = Market(Mat, dc: 10, dcWorld: Zodiark),
        };

        // 4 items -> 2 crafts -> 6 items produced, 4 materials = 40 gil
        var analysis = Calc(data, market, batch: 4).Analyze(Product);

        Assert.Equal(40.0 / 6, analysis.Craft!.Cost, 6);
        Assert.Equal(4, analysis.CraftLines.Single().TotalUnits);
    }

    [Fact]
    public void Intermediates_are_costed_for_the_units_needed()
    {
        var data = new Data();
        data.AddRecipe(Product, 1, (Mid, 2));
        data.AddRecipe(Mid, 2, (Leaf, 3)); // 2 Mid per craft
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mid] = Market(Mid, dc: 500, dcWorld: Zodiark),
            [Leaf] = Market(Leaf, dc: 10, dcWorld: Zodiark),
        };

        // 5 items need 10 Mid -> 5 crafts -> 15 Leaf = 150, much cheaper than 10 x 500
        var analysis = Calc(data, market, batch: 5).Analyze(Product);

        Assert.Equal(30, analysis.Craft!.Cost, 6); // 150 / 5
        Assert.Equal(CostSource.Crafted, analysis.CraftLines.Single().Source);
    }

    [Fact]
    public void Gathered_materials_stay_free_whatever_the_quantity()
    {
        var data = new Data();
        data.AddRecipe(Product, 1, (Mat, 4));
        data.Gatherable.Add(Mat);
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, world: 1000),
            [Mat] = Market(Mat, dc: 10, dcWorld: Zodiark),
        };

        Assert.Equal(0, Calc(data, market, batch: 50).Analyze(Product).Gather!.Cost, 6);
    }
}

public class RobustSellPriceTests
{
    private const uint Product = 1;

    private sealed class Data : IRecipeSource
    {
        public IReadOnlyList<Recipe> GetRecipes(uint itemId) => [];
        public bool IsGatherable(uint itemId) => false;
        public long? GetVendorPrice(uint itemId) => null;
        public bool CanBeHq(uint itemId) => true;
        public string GetItemName(uint itemId) => "x";
    }

    private static ItemAnalysis Analyze(
        long lowest, long? average, IEnumerable<(long Price, bool Hq)> listings, SellPriceBasis basis, bool preferHq = false)
    {
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = new ItemMarket(
                Product,
                new QualityData(lowest, null, null, average, 5, 5),
                new QualityData(lowest, null, null, average, 5, 5),
                null),
        };
        var worldListings = new Dictionary<uint, ItemListings>
        {
            [Product] = new ItemListings(Product, listings.Select(l => new Listing(l.Price, 1, 0, l.Hq)).OrderBy(l => l.PricePerUnit).ToList()),
        };

        return new ProfitCalculator(
                new Data(),
                id => market.GetValueOrDefault(id),
                new PricingSettings(preferHq, 0, basis),
                10,
                null,
                id => worldListings.GetValueOrDefault(id))
            .Analyze(Product);
    }

    [Fact]
    public void A_single_lowball_does_not_set_the_price()
    {
        var analysis = Analyze(1, 520, [(1, false), (500, false), (520, false), (530, false), (540, false), (900, false)], SellPriceBasis.Robust);

        Assert.Equal(520, analysis.WorldPrice); // median of the cheapest five
        Assert.Equal(1, analysis.LowestListing);
    }

    [Fact]
    public void Lowest_listing_basis_still_takes_the_lowball()
    {
        var analysis = Analyze(1, 520, [(1, false), (500, false), (520, false)], SellPriceBasis.LowestListing);

        Assert.Equal(1, analysis.WorldPrice);
    }

    [Fact]
    public void With_fewer_than_three_listings_the_lowest_is_used()
    {
        Assert.Equal(100, Analyze(100, 100, [(100, false), (900, false)], SellPriceBasis.Robust).WorldPrice);
    }

    [Fact]
    public void Only_listings_of_the_sold_quality_count()
    {
        var listings = new[] { (10L, true), (400L, false), (410L, false), (420L, false), (430L, true), (440L, true), (450L, true) };

        Assert.Equal(410, Analyze(10, null, listings, SellPriceBasis.Robust).WorldPrice); // NQ: 400, 410, 420
        Assert.Equal(435, Analyze(10, null, listings, SellPriceBasis.Robust, preferHq: true).WorldPrice); // HQ: 10, 430, 440, 450 -> (430 + 440) / 2
    }

    [Fact]
    public void Missing_listings_fall_back_to_the_lowest_price()
    {
        Assert.Equal(300, Analyze(300, 310, [], SellPriceBasis.Robust).WorldPrice);
    }

    [Fact]
    public void Deviation_from_the_recent_average_is_reported()
    {
        var below = Analyze(70, 100, [], SellPriceBasis.LowestListing);
        Assert.Equal(-30, below.PriceDeviationPercent!.Value, 6);

        var above = Analyze(150, 100, [], SellPriceBasis.LowestListing);
        Assert.Equal(50, above.PriceDeviationPercent!.Value, 6);

        Assert.Null(Analyze(100, null, [], SellPriceBasis.LowestListing).PriceDeviationPercent);
    }
}

public class PopularCacheTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "gilplanner-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    private static PopularScan Scan(uint world, DateTimeOffset at) =>
        new(world, at, [new PopularItem(5, 100, true, 12.5, 1250), new PopularItem(6, null, false, 3, 0)]);

    [Fact]
    public void Round_trips_through_disk()
    {
        var path = Path.Combine(dir, "nested", "cache.json");
        var at = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        new PopularCache(path).Store(Scan(66, at));

        var loaded = new PopularCache(path).Get(66)!;

        Assert.Equal(at, loaded.ScannedAt);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(new PopularItem(5, 100, true, 12.5, 1250), loaded.Items[0]);
        Assert.Null(loaded.Items[1].Price);
        Assert.Null(new PopularCache(path).Get(1));
    }

    [Fact]
    public void Missing_or_corrupt_files_are_ignored()
    {
        Assert.Null(new PopularCache(Path.Combine(dir, "none.json")).Get(1));

        Directory.CreateDirectory(dir);
        var corrupt = Path.Combine(dir, "corrupt.json");
        File.WriteAllText(corrupt, "{ this is not json");
        var cache = new PopularCache(corrupt);
        Assert.Null(cache.Get(1));

        cache.Store(Scan(1, DateTimeOffset.UtcNow));
        Assert.NotNull(new PopularCache(corrupt).Get(1));
    }

    [Fact]
    public void Keeps_only_the_five_most_recent_worlds()
    {
        var path = Path.Combine(dir, "cache.json");
        var cache = new PopularCache(path);
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        for (uint world = 1; world <= 7; world++)
            cache.Store(Scan(world, start.AddHours(world)));

        var reloaded = new PopularCache(path);
        Assert.Null(reloaded.Get(1));
        Assert.Null(reloaded.Get(2));
        Assert.NotNull(reloaded.Get(3));
        Assert.NotNull(reloaded.Get(7));
    }

    [Fact]
    public void Service_restore_shows_cached_results_without_scanning()
    {
        using var client = new UniversalisClient("test");
        using var service = new PopularService(client);
        var at = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

        service.Restore(Scan(66, at));

        Assert.Equal(66u, service.WorldId);
        Assert.Equal(at, service.LastScanned);
        Assert.Equal(2, service.Results.Count);
        Assert.False(service.IsScanning);
    }
}
