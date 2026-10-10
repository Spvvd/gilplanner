using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using GilPlanner.Pricing;
using Lumina.Excel.Sheets;
using Recipe = GilPlanner.Pricing.Recipe;

namespace GilPlanner;

public sealed record ItemInfo(uint Id, string Name, ushort IconId, bool CanBeHq, bool IsMarketable);

/// <summary>Static game data (recipes, vendors, gatherables, names) read from the game files via Lumina.</summary>
public sealed class GameData : IRecipeSource
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, List<Recipe>> recipesByResult = new();
    private readonly HashSet<uint> gatherable = new();
    private readonly Dictionary<uint, long> vendorPrices = new();
    private readonly Dictionary<uint, string> worldNames = new();
    private readonly Dictionary<uint, string> dataCenterNames = new();
    private readonly Dictionary<uint, ItemInfo> itemCache = new();

    private static readonly IReadOnlyList<Recipe> NoRecipes = [];

    public GameData(IDataManager dataManager)
    {
        this.dataManager = dataManager;

        LoadRecipes();
        LoadGatherables();
        LoadVendorPrices();
        LoadWorlds();
    }

    /// <summary>Marketable items that have at least one recipe, sorted by name (for the search box).</summary>
    public IReadOnlyList<ItemInfo> CraftableItems { get; private set; } = [];

    public IReadOnlyList<Recipe> GetRecipes(uint itemId) =>
        recipesByResult.TryGetValue(itemId, out var recipes) ? recipes : NoRecipes;

    public bool IsGatherable(uint itemId) => gatherable.Contains(itemId);

    public long? GetVendorPrice(uint itemId) => vendorPrices.TryGetValue(itemId, out var price) ? price : null;

    public bool CanBeHq(uint itemId) => GetItem(itemId)?.CanBeHq ?? false;

    public string GetItemName(uint itemId) => GetItem(itemId)?.Name ?? $"Item #{itemId}";

    public string GetWorldName(uint worldId) => worldNames.TryGetValue(worldId, out var name) ? name : $"World #{worldId}";

    public string GetDataCenterName(uint worldId) => dataCenterNames.TryGetValue(worldId, out var name) ? name : string.Empty;

    public bool IsCraftableAndMarketable(uint itemId) =>
        recipesByResult.ContainsKey(itemId) && GetItem(itemId)?.IsMarketable == true;

    public ItemInfo? GetItem(uint itemId)
    {
        lock (itemCache)
        {
            if (itemCache.TryGetValue(itemId, out var cached))
                return cached;

            var row = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
            if (row is not { } item)
                return null;

            var info = new ItemInfo(
                itemId,
                item.Name.ToString(),
                item.Icon,
                item.CanBeHq,
                item.ItemSearchCategory.RowId != 0);
            itemCache[itemId] = info;
            return info;
        }
    }

    private void LoadRecipes()
    {
        foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Recipe>())
        {
            var resultId = row.ItemResult.RowId;
            if (resultId == 0)
                continue;

            var ingredients = new List<Ingredient>();
            for (var i = 0; i < row.Ingredient.Count; i++)
            {
                var id = row.Ingredient[i].RowId;
                var amount = row.AmountIngredient[i];
                if (id != 0 && amount != 0)
                    ingredients.Add(new Ingredient(id, amount));
            }

            if (ingredients.Count == 0)
                continue;

            if (!recipesByResult.TryGetValue(resultId, out var list))
                recipesByResult[resultId] = list = [];
            list.Add(new Recipe(row.RowId, resultId, Math.Max(1, (int)row.AmountResult), ingredients));
        }

        CraftableItems = recipesByResult.Keys
            .Select(GetItem)
            .OfType<ItemInfo>()
            .Where(i => i.IsMarketable && i.Name.Length > 0)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadGatherables()
    {
        foreach (var row in dataManager.GetExcelSheet<GatheringItem>())
        {
            if (row.Item.RowId > 0)
                gatherable.Add(row.Item.RowId);
        }

        foreach (var row in dataManager.GetExcelSheet<SpearfishingItem>())
        {
            if (row.Item.RowId > 0)
                gatherable.Add(row.Item.RowId);
        }

        foreach (var row in dataManager.GetExcelSheet<FishParameter>())
        {
            if (row.Item.RowId > 0)
                gatherable.Add(row.Item.RowId);
        }
    }

    private void LoadVendorPrices()
    {
        var items = dataManager.GetExcelSheet<Item>();
        foreach (var shop in dataManager.GetSubrowExcelSheet<GilShopItem>())
        {
            foreach (var entry in shop)
            {
                var id = entry.Item.RowId;
                if (id == 0 || vendorPrices.ContainsKey(id))
                    continue;

                if (items.GetRowOrDefault(id) is { PriceMid: > 0 } item)
                    vendorPrices[id] = item.PriceMid;
            }
        }
    }

    private void LoadWorlds()
    {
        foreach (var world in dataManager.GetExcelSheet<World>())
        {
            if (!world.IsPublic)
                continue;

            worldNames[world.RowId] = world.Name.ToString();
            if (world.DataCenter.ValueNullable is { } dataCenter)
                dataCenterNames[world.RowId] = dataCenter.Name.ToString();
        }
    }
}
