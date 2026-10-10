using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GilPlanner.Pricing;

/// <summary>Parses the response of <c>/api/v2/aggregated/{world}/{ids}</c>.</summary>
public static class UniversalisParser
{
    public static Dictionary<uint, ItemMarket> Parse(string json, uint worldId)
    {
        var result = new Dictionary<uint, ItemMarket>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("itemId", out var idElement) || !idElement.TryGetUInt32(out var itemId))
                continue;

            var nq = item.TryGetProperty("nq", out var nqElement) ? ParseQuality(nqElement) : QualityData.Empty;
            var hq = item.TryGetProperty("hq", out var hqElement) ? ParseQuality(hqElement) : QualityData.Empty;
            var uploadTimes = ReadUploadTimes(item);
            result[itemId] = new ItemMarket(
                itemId,
                nq,
                hq,
                uploadTimes.TryGetValue(worldId, out var ownUpload) ? ownUpload : null,
                uploadTimes);
        }

        return result;
    }

    /// <summary>
    /// Parses the current-listings endpoint. A request for several items nests them under <c>items</c>; a request
    /// for a single item returns that item at the top level.
    /// </summary>
    public static Dictionary<uint, ItemListings> ParseListings(string json)
    {
        var result = new Dictionary<uint, ItemListings>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in items.EnumerateObject())
                AddListings(entry.Value, result, uint.TryParse(entry.Name, out var key) ? key : null);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            AddListings(root, result, null);
        }

        return result;
    }

    private static void AddListings(JsonElement item, Dictionary<uint, ItemListings> result, uint? fallbackId)
    {
        var itemId = item.TryGetProperty("itemID", out var idElement) && idElement.TryGetUInt32(out var parsed) ? parsed : fallbackId;
        if (itemId is null)
            return;

        var listings = new List<Listing>();
        if (item.TryGetProperty("listings", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                // Mannequin listings are displays, not for sale.
                if (entry.TryGetProperty("onMannequin", out var mannequin) && mannequin.ValueKind == JsonValueKind.True)
                    continue;

                if (!entry.TryGetProperty("pricePerUnit", out var price) || !price.TryGetInt64(out var pricePerUnit)
                    || !entry.TryGetProperty("quantity", out var quantity) || !quantity.TryGetInt32(out var qty) || qty <= 0)
                    continue;

                var world = entry.TryGetProperty("worldID", out var w) && w.TryGetUInt32(out var worldId) ? worldId : 0;
                var hq = entry.TryGetProperty("hq", out var h) && h.ValueKind == JsonValueKind.True;
                listings.Add(new Listing(pricePerUnit, qty, world, hq));
            }
        }

        listings.Sort((a, b) => a.PricePerUnit.CompareTo(b.PricePerUnit));
        result[itemId.Value] = new ItemListings(itemId.Value, listings);
    }

    private static QualityData ParseQuality(JsonElement quality) => new(
        WorldMin: Number(quality, "minListing", "world", "price"),
        DcMin: Number(quality, "minListing", "dc", "price"),
        DcMinWorldId: Number(quality, "minListing", "dc", "worldId") is { } id ? (uint)id : null,
        WorldAvg: Number(quality, "averageSalePrice", "world", "price"),
        WorldVelocity: NumberD(quality, "dailySaleVelocity", "world", "quantity") ?? 0,
        DcVelocity: NumberD(quality, "dailySaleVelocity", "dc", "quantity") ?? 0);

    private static Dictionary<uint, DateTimeOffset> ReadUploadTimes(JsonElement item)
    {
        var times = new Dictionary<uint, DateTimeOffset>();
        if (!item.TryGetProperty("worldUploadTimes", out var array) || array.ValueKind != JsonValueKind.Array)
            return times;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.TryGetProperty("worldId", out var id) && id.TryGetUInt32(out var world)
                && entry.TryGetProperty("timestamp", out var ts) && ts.TryGetDouble(out var ms))
                times[world] = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }

        return times;
    }

    private static long? Number(JsonElement root, params string[] path) =>
        NumberD(root, path) is { } value ? (long)Math.Round(value) : null;

    // Missing blocks and empty objects ({}) both mean "no data", so any miss along the path returns null.
    private static double? NumberD(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out var value) ? value : null;
    }
}
