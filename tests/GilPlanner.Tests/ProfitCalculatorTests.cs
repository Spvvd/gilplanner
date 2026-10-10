using GilPlanner.Pricing;

namespace GilPlanner.Tests;

public class ProfitCalculatorTests
{
    private const uint MyWorld = 10;
    private const uint OtherWorld = 20;

    // Item ids used below
    private const uint Product = 1, MatA = 2, MatB = 3, Mid = 4, Crystal = 5;

    private sealed class FakeGameData : IRecipeSource
    {
        public Dictionary<uint, List<Recipe>> Recipes { get; } = new();
        public HashSet<uint> Gatherable { get; } = new();
        public Dictionary<uint, long> Vendor { get; } = new();
        public HashSet<uint> Hq { get; } = new();

        public IReadOnlyList<Recipe> GetRecipes(uint itemId) => Recipes.GetValueOrDefault(itemId) ?? [];
        public bool IsGatherable(uint itemId) => Gatherable.Contains(itemId);
        public long? GetVendorPrice(uint itemId) => Vendor.TryGetValue(itemId, out var p) ? p : null;
        public bool CanBeHq(uint itemId) => Hq.Contains(itemId);
        public string GetItemName(uint itemId) => $"item{itemId}";

        public void AddRecipe(uint result, int yield, params (uint Id, int Amount)[] ingredients) =>
            Recipes.GetOrAdd(result).Add(new Recipe(result * 100 + (uint)Recipes[result].Count, result, yield, ingredients.Select(i => new Ingredient(i.Id, i.Amount)).ToList()));
    }

    private static QualityData Quality(long? world = null, long? dc = null, uint? dcWorld = null, long? avg = null, double velocity = 0) =>
        new(world, dc, dcWorld, avg, velocity, velocity);

    private static ItemMarket Market(uint id, QualityData? nq = null, QualityData? hq = null) =>
        new(id, nq ?? QualityData.Empty, hq ?? QualityData.Empty, null);

    private static ProfitCalculator Calc(
        FakeGameData data,
        Dictionary<uint, ItemMarket> market,
        double tax = 0,
        bool preferHq = false,
        SellPriceBasis basis = SellPriceBasis.LowestListing) =>
        new(data, id => market.GetValueOrDefault(id), new PricingSettings(preferHq, tax, basis), MyWorld);

    [Fact]
    public void Flip_is_sell_price_after_tax_minus_cheapest_dc_price()
    {
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000, dc: 600, dcWorld: OtherWorld)),
        };

        var flip = Calc(new FakeGameData(), market, tax: 5).Analyze(Product).Flip!;

        Assert.Equal(350, flip.Gil, 6); // 950 - 600
        Assert.Equal(350.0 / 600 * 100, flip.Percent!.Value, 6);
    }

    [Fact]
    public void Flip_is_unavailable_when_my_world_already_has_the_cheapest_listing()
    {
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 600, dc: 600, dcWorld: MyWorld)),
        };

        Assert.Null(Calc(new FakeGameData(), market).Analyze(Product).Flip);
    }

    [Fact]
    public void Craft_cost_is_divided_by_recipe_yield()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 2, (MatA, 4));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000)),
            [MatA] = Market(MatA, Quality(world: 150, dc: 100, dcWorld: OtherWorld)),
        };

        var craft = Calc(data, market, tax: 5).Analyze(Product).Craft!;

        Assert.Equal(200, craft.Cost, 6); // 4 x 100 / 2
        Assert.Equal(750, craft.Gil, 6); // 950 - 200
        Assert.Equal(375, craft.Percent!.Value, 6);
    }

    [Fact]
    public void Gather_mode_values_gatherables_at_zero_and_vendor_items_at_npc_price()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (MatA, 3), (MatB, 2), (Crystal, 1));
        data.Gatherable.Add(MatA);
        data.Vendor[MatB] = 30;
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000)),
            [MatA] = Market(MatA, Quality(dc: 50, dcWorld: OtherWorld)),
            [MatB] = Market(MatB, Quality(dc: 500, dcWorld: OtherWorld)),
            [Crystal] = Market(Crystal, Quality(dc: 10, dcWorld: OtherWorld)),
        };

        var analysis = Calc(data, market).Analyze(Product);

        Assert.Equal(70, analysis.Gather!.Cost, 6); // 0 + 2*30 + 10
        Assert.Equal(3 * 50 + 2 * 30 + 10, analysis.Craft!.Cost, 6); // gatherable is bought in craft mode
        Assert.Contains(analysis.GatherLines, l => l.ItemId == MatA && l.Source == CostSource.Gathered);
        Assert.Contains(analysis.GatherLines, l => l.ItemId == MatB && l.Source == CostSource.Vendor);
        Assert.Contains(analysis.CraftLines, l => l.ItemId == MatB && l.Source == CostSource.Vendor);
    }

    [Fact]
    public void Percent_is_null_when_everything_is_free()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (MatA, 1));
        data.Gatherable.Add(MatA);
        var market = new Dictionary<uint, ItemMarket> { [Product] = Market(Product, Quality(world: 100)) };

        var gather = Calc(data, market).Analyze(Product).Gather!;

        Assert.Equal(100, gather.Gil, 6);
        Assert.Null(gather.Percent);
    }

    [Fact]
    public void Intermediate_is_crafted_when_cheaper_than_buying_it()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (Mid, 1));
        data.AddRecipe(Mid, 1, (MatA, 1), (MatB, 1));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000)),
            [Mid] = Market(Mid, Quality(dc: 900, dcWorld: OtherWorld)),
            [MatA] = Market(MatA, Quality(dc: 30, dcWorld: OtherWorld)),
            [MatB] = Market(MatB, Quality(dc: 20, dcWorld: OtherWorld)),
        };

        var craft = Calc(data, market).Analyze(Product).Craft!;

        Assert.Equal(50, craft.Cost, 6);
        Assert.Contains(Calc(data, market).Analyze(Product).CraftLines, l => l.ItemId == Mid && l.Source == CostSource.Crafted);
    }

    [Fact]
    public void Intermediate_is_bought_when_cheaper_than_crafting_it()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (Mid, 1));
        data.AddRecipe(Mid, 1, (MatA, 1));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000)),
            [Mid] = Market(Mid, Quality(dc: 40, dcWorld: OtherWorld)),
            [MatA] = Market(MatA, Quality(dc: 300, dcWorld: OtherWorld)),
        };

        var analysis = Calc(data, market).Analyze(Product);

        Assert.Equal(40, analysis.Craft!.Cost, 6);
        Assert.Contains(analysis.CraftLines, l => l.ItemId == Mid && l.Source == CostSource.Market && l.WorldId == OtherWorld);
    }

    [Fact]
    public void Missing_material_price_makes_crafting_profit_unavailable_but_keeps_the_breakdown()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (MatA, 1), (MatB, 1));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000)),
            [MatA] = Market(MatA, Quality(dc: 10, dcWorld: OtherWorld)),
        };

        var analysis = Calc(data, market).Analyze(Product);

        Assert.Null(analysis.Craft);
        Assert.Contains(analysis.CraftLines, l => l.ItemId == MatB && l.Source == CostSource.Unavailable);
    }

    [Fact]
    public void Circular_recipes_terminate()
    {
        var data = new FakeGameData();
        data.AddRecipe(MatA, 1, (MatB, 1));
        data.AddRecipe(MatB, 1, (MatA, 1));
        data.AddRecipe(Product, 1, (MatA, 1));
        var market = new Dictionary<uint, ItemMarket> { [Product] = Market(Product, Quality(world: 1000)) };

        var analysis = Calc(data, market).Analyze(Product);

        Assert.Null(analysis.Craft);
    }

    [Fact]
    public void Cheapest_of_multiple_recipes_is_used()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (MatA, 1));
        data.AddRecipe(Product, 1, (MatB, 1));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 1000)),
            [MatA] = Market(MatA, Quality(dc: 200, dcWorld: OtherWorld)),
            [MatB] = Market(MatB, Quality(dc: 80, dcWorld: OtherWorld)),
        };

        Assert.Equal(80, Calc(data, market).Analyze(Product).Craft!.Cost, 6);
    }

    [Fact]
    public void Hq_is_used_when_preferred_and_available_otherwise_nq()
    {
        var data = new FakeGameData();
        data.Hq.Add(Product);
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 100, velocity: 1), Quality(world: 400, velocity: 9)),
        };

        var withHq = Calc(data, market, preferHq: true).Analyze(Product);
        Assert.True(withHq.IsHq);
        Assert.Equal(400, withHq.WorldPrice);
        Assert.Equal(9, withHq.Velocity);

        var nqOnly = new Dictionary<uint, ItemMarket> { [Product] = Market(Product, Quality(world: 100)) };
        var fallback = Calc(data, nqOnly, preferHq: true).Analyze(Product);
        Assert.False(fallback.IsHq);
        Assert.Equal(100, fallback.WorldPrice);
    }

    [Fact]
    public void Average_sale_basis_uses_the_average_and_falls_back_to_lowest_listing()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (MatA, 1));
        var market = new Dictionary<uint, ItemMarket>
        {
            [Product] = Market(Product, Quality(world: 100, avg: 300)),
            [MatA] = Market(MatA, Quality(dc: 50, dcWorld: OtherWorld)),
        };

        Assert.Equal(250, Calc(data, market, basis: SellPriceBasis.AverageSale).Analyze(Product).Craft!.Gil, 6);

        market[Product] = Market(Product, Quality(world: 100));
        Assert.Equal(50, Calc(data, market, basis: SellPriceBasis.AverageSale).Analyze(Product).Craft!.Gil, 6);
    }

    [Fact]
    public void Item_without_market_data_has_no_profits()
    {
        var analysis = Calc(new FakeGameData(), []).Analyze(Product);

        Assert.False(analysis.HasData);
        Assert.Null(analysis.Craft);
    }

    [Fact]
    public void Collects_materials_of_materials_but_stops_at_max_depth()
    {
        var data = new FakeGameData();
        data.AddRecipe(Product, 1, (Mid, 1));
        data.AddRecipe(Mid, 1, (MatA, 1));
        data.AddRecipe(MatA, 1, (MatB, 1));
        data.AddRecipe(MatB, 1, (Crystal, 1));
        // Chain of 5 deep: Product -> Mid -> MatA -> MatB -> Crystal -> 6 (never reached).
        data.AddRecipe(Crystal, 1, (6, 1));

        using var client = new UniversalisClient("test");
        using var service = new MarketService(client, data);

        var ids = service.CollectItemIds([Product]);

        Assert.Contains(Crystal, ids);
        Assert.DoesNotContain(6u, ids);
    }
}

internal static class DictionaryExtensions
{
    public static List<T> GetOrAdd<TKey, T>(this Dictionary<TKey, List<T>> dict, TKey key) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        return list;
    }
}
