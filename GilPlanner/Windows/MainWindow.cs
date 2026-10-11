using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GilPlanner.Pricing;

namespace GilPlanner.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.35f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.8f, 0.3f, 1f);

    private const int ColItem = 0, ColWorld = 1, ColDc = 2, ColFlip = 3, ColCraft = 4, ColGather = 5, ColSales = 6, ColProfitDay = 7, ColAge = 8;
    private const int MaxSearchResults = 12;

    /// <summary>A sell price this far (in percent) from the recent average sale price gets a warning marker.</summary>
    private const double DeviationWarningPercent = 30;

    private const int MaxPurchasesShown = 4;

    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ITextureProvider textureProvider;
    private readonly Planner planner;
    private readonly ItemActions actions;

    private bool selectSettingsTab;
    private string searchText = string.Empty;
    private const int PopItem = 0, PopPrice = 1, PopSales = 2, PopGilPerDay = 3;

    private int sortColumn = ColCraft;
    private bool sortAscending;
    private int popularSortColumn = PopSales;
    private bool popularSortAscending;

    public MainWindow(
        Configuration configuration,
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        Planner planner,
        ItemActions actions)
        : base("Gil Planner##GilPlannerMain")
    {
        this.configuration = configuration;
        this.pluginInterface = pluginInterface;
        this.textureProvider = textureProvider;
        this.planner = planner;
        this.actions = actions;

        Size = new Vector2(1100, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 240) };

        // Only open on plugin load when asked to; /gilplanner toggles it at any time.
        IsOpen = configuration.OpenOnStartup;
    }

    /// <summary>Opens the window on the Settings tab (the plugin installer's settings button).</summary>
    public void OpenSettings()
    {
        selectSettingsTab = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        planner.Tick();

        if (!ImGui.BeginTabBar("##tabs"))
            return;

        if (ImGui.BeginTabItem("Market"))
        {
            DrawMarketTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Popular"))
        {
            DrawPopularTab();
            ImGui.EndTabItem();
        }

        // SetSelected only applies for the one frame it is passed, which is what opening the settings needs.
        var settingsFlags = selectSettingsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectSettingsTab = false;
        if (ImGui.BeginTabItem("Settings", settingsFlags))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawMarketTab()
    {
        DrawStatusBar();
        DrawSearch();
        ImGui.Separator();

        if (planner.Analyses.Count == 0)
        {
            ImGui.TextColored(Dim, "No items yet. Right-click a craftable item and pick \"Add to Gil Planner\", or search above.");
            return;
        }

        DrawTable();
    }

    private void DrawStatusBar()
    {
        var market = planner.Market;
        var world = planner.PricingWorld();

        var cooldown = planner.RefreshCooldownRemaining;
        ImGui.BeginDisabled(market.IsRefreshing || world is null || planner.Analyses.Count == 0 || cooldown > TimeSpan.Zero);
        if (ImGui.Button("Refresh"))
            planner.Refresh();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && cooldown > TimeSpan.Zero && !market.IsRefreshing)
            ImGui.SetTooltip($"Wait {Math.Ceiling(cooldown.TotalSeconds):0} s before refreshing again.");

        ImGui.SameLine();
        if (world is null)
            ImGui.TextColored(Dim, "Log in to a character to load prices.");
        else if (market.IsRefreshing)
            ImGui.TextColored(Dim, $"{world.Value.Name}: refreshing...");
        else if (!planner.HasDataForPricingWorld)
            ImGui.TextColored(Warn, configuration.AutoRefresh
                ? $"{world.Value.Name}: no prices loaded yet."
                : $"{world.Value.Name}: auto-refresh is off, press Refresh to load prices.");
        else if (planner.DataAge is { } age)
            ImGui.TextColored(
                FreshnessColor(planner.Freshness),
                $"{world.Value.Name} ({(configuration.UseHomeWorld ? "home world" : "current world")}, cheapest prices from its data center): updated {FormatAge(age)}"
                + (configuration.AutoRefresh ? string.Empty : " (auto-refresh off)"));

        DrawHealthBanner(world is not null);
    }

    /// <summary>Explains failed updates and old prices, so numbers that may be wrong never look current.</summary>
    private void DrawHealthBanner(bool loggedIn)
    {
        var market = planner.Market;
        if (!loggedIn || market.IsRefreshing)
            return;

        var failure = market.Failure;
        var freshness = planner.Freshness;
        if (failure is null && freshness == Freshness.Fresh)
            return;

        var severe = freshness == Freshness.VeryStale || market.AutoRefreshPaused || !planner.HasDataForPricingWorld;
        var color = severe ? Red : Warn;

        ImGui.BeginGroup();
        if (failure is { } f)
        {
            var have = planner.DataAge is { } age
                ? $"Showing prices from {FormatAge(age)}, they may be out of date."
                : "No prices could be loaded.";
            ImGui.TextColored(color, $"! Could not update prices: {f.Message}. {have}");

            if (market.AutoRefreshPaused)
                ImGui.TextColored(color, $"  Automatic refresh is paused after {market.ConsecutiveFailures} failed attempts. Press Refresh to try again.");
            else if (configuration.AutoRefresh && market.NextRetryAt is { } next)
                ImGui.TextColored(Dim, $"  Next automatic retry in {FormatDuration(next - DateTimeOffset.UtcNow)}, or press Refresh.");
            else
                ImGui.TextColored(Dim, "  Press Refresh to try again.");

            if (market.ListingsFailed)
                ImGui.TextColored(Warn, "  The detailed listings failed: material costs ignore batch size and the sell price is the lowest listing.");
        }
        else if (planner.DataAge is { } age)
        {
            ImGui.TextColored(color, $"! Prices are {FormatSpan(age)} old and may no longer be right.");
            ImGui.TextColored(Dim, configuration.AutoRefresh ? "  A refresh is overdue." : "  Auto-refresh is off, press Refresh.");
        }

        ImGui.EndGroup();
        if (failure is { } detail && ImGui.IsItemHovered())
            ImGui.SetTooltip($"Technical detail: {detail.Detail}");
    }

    private void DrawSearch()
    {
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##search", "Add craftable item by name...", ref searchText, 64);

        if (searchText.Trim().Length < 2)
            return;

        var query = searchText.Trim();
        var matches = planner.GameData.CraftableItems
            .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSearchResults)
            .ToList();

        ImGui.SameLine();
        ImGui.TextColored(Dim, matches.Count == 0 ? "No match." : "Click a result to add it:");

        foreach (var item in matches)
        {
            var tracked = planner.IsTracked(item.Id);
            ImGui.BeginDisabled(tracked);
            if (ImGui.Selectable($"{item.Name}{(tracked ? "  (added)" : string.Empty)}##search{item.Id}"))
            {
                planner.Add(item.Id);
                searchText = string.Empty;
            }

            ImGui.EndDisabled();
        }
    }

    private void DrawTable()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##items", 9, flags, ImGui.GetContentRegionAvail()))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("My world", ImGuiTableColumnFlags.PreferSortDescending, 1f);
        ImGui.TableSetupColumn("Cheapest (DC)", ImGuiTableColumnFlags.PreferSortDescending, 1.3f);
        ImGui.TableSetupColumn("Flip profit", ImGuiTableColumnFlags.PreferSortDescending, 1.5f);
        ImGui.TableSetupColumn("Craft profit", ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.DefaultSort, 1.5f);
        ImGui.TableSetupColumn("Gather profit", ImGuiTableColumnFlags.PreferSortDescending, 1.5f);
        ImGui.TableSetupColumn("Sales/day", ImGuiTableColumnFlags.PreferSortDescending, 0.9f);
        ImGui.TableSetupColumn("Profit/day", ImGuiTableColumnFlags.PreferSortDescending, 1.2f);
        ImGui.TableSetupColumn("Data age", ImGuiTableColumnFlags.None, 0.8f);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            sortColumn = specs.Specs.ColumnIndex;
            sortAscending = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }

        foreach (var analysis in Sorted(planner.Analyses))
        {
            ImGui.TableNextRow();
            DrawRow(analysis);
        }

        ImGui.EndTable();
    }

    private IEnumerable<ItemAnalysis> Sorted(IReadOnlyList<ItemAnalysis> rows)
    {
        // Rows without a value always sink to the bottom, whichever way the column is sorted.
        double? Key(ItemAnalysis a) => sortColumn switch
        {
            ColWorld => a.WorldPrice,
            ColDc => a.DcPrice,
            ColFlip => a.Flip?.Gil,
            ColCraft => a.Craft?.Gil,
            ColGather => a.Gather?.Gil,
            ColSales => a.HasData ? a.Velocity : null,
            ColProfitDay => a.DailyProfit,
            ColAge => a.UploadTime is { } uploaded ? (DateTimeOffset.UtcNow - uploaded).TotalSeconds : null,
            _ => null,
        };

        if (sortColumn == ColItem)
        {
            return sortAscending
                ? rows.OrderBy(a => planner.GameData.GetItemName(a.ItemId), StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(a => planner.GameData.GetItemName(a.ItemId), StringComparer.OrdinalIgnoreCase);
        }

        var withValue = rows.Where(a => Key(a) is not null);
        var sorted = sortAscending ? withValue.OrderBy(a => Key(a)) : withValue.OrderByDescending(a => Key(a));
        return sorted.Concat(rows.Where(a => Key(a) is null));
    }

    private void DrawRow(ItemAnalysis a)
    {
        var data = planner.GameData;
        var info = data.GetItem(a.ItemId);

        ImGui.TableNextColumn();
        if (info is not null)
        {
            var icon = textureProvider.GetFromGameIcon(new GameIconLookup(info.IconId)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new Vector2(24, 24));
            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Selectable(
            $"{data.GetItemName(a.ItemId)}{(a.IsHq ? " (HQ)" : string.Empty)}##row{a.ItemId}",
            false,
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap);
        DrawItemContextMenu(a.ItemId, a.IsHq, inList: true);

        if (!a.HasData)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(Dim, planner.Market.IsRefreshing || planner.IsRefreshPending ? "loading..." : "no data");
            return;
        }

        var freshness = planner.Freshness;

        ImGui.TableNextColumn();
        DrawSellPrice(a, freshness);

        ImGui.TableNextColumn();
        DrawCheapestInDc(a);

        ImGui.TableNextColumn();
        DrawProfit(a.Flip, freshness);

        ImGui.TableNextColumn();
        DrawMaterialsCell(a, a.Craft, a.CraftLines, freshness, "craft", "Craft: buy materials at the cheapest listings in your DC");

        ImGui.TableNextColumn();
        DrawMaterialsCell(a, a.Gather, a.GatherLines, freshness, "gather", "Gather: gatherable materials cost 0 gil");

        ImGui.TableNextColumn();
        var color = a.Velocity < 1 ? Warn : (Vector4?)null;
        var text = a.Velocity.ToString("0.0", CultureInfo.InvariantCulture);
        if (color is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Sales per day on your world ({(a.IsHq ? "HQ" : "NQ")}), from Universalis' recent sale history.");

        ImGui.TableNextColumn();
        DrawDailyProfit(a, freshness);

        ImGui.TableNextColumn();
        DrawDataAge(a);
    }

    /// <summary>The cheapest DC price and its world. The world is flagged when its data was last uploaded long ago.</summary>
    private void DrawCheapestInDc(ItemAnalysis a)
    {
        var world = a.DcWorldId is { } id ? planner.GameData.GetWorldName(id) : null;
        var uploadFreshness = a.DcWorldUploadTime is { } uploaded
            ? DataFreshness.ClassifyUpload(DateTimeOffset.UtcNow - uploaded)
            : Freshness.Fresh;

        ImGui.BeginGroup();
        DrawPrice(a.DcPrice);
        if (a.DcPrice is not null && world is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(FreshnessColor(uploadFreshness), (uploadFreshness == Freshness.Fresh ? string.Empty : "! ") + world);
        }

        ImGui.EndGroup();

        if (!ImGui.IsItemHovered() || world is null || a.DcPrice is null)
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Cheapest in your data center: {Gil(a.DcPrice.Value)} on {world}");
        if (a.DcWorldUploadTime is { } up)
        {
            ImGui.TextColored(
                FreshnessColor(uploadFreshness),
                $"Market data of {world} for this item was last uploaded {FormatAge(DateTimeOffset.UtcNow - up)}.");
            if (uploadFreshness != Freshness.Fresh)
                ImGui.TextColored(Warn, "This listing may no longer exist. Check it on the market board before travelling.");
        }

        ImGui.EndTooltip();
    }

    /// <summary>Estimated profit per day of the best strategy, scaled to the share of sales you expect to get.</summary>
    private void DrawDailyProfit(ItemAnalysis a, Freshness freshness)
    {
        if (a.DailyProfit is not { } daily || a.BestStrategy is not { } strategy)
        {
            ImGui.TextColored(Dim, "-");
            return;
        }

        var text = $"{(daily >= 0 ? "+" : "-")}{Gil(Math.Abs(daily))}";
        ImGui.TextColored(freshness != Freshness.Fresh ? Dim : daily >= 0 ? Green : Red, text);
        if (!ImGui.IsItemHovered())
            return;

        var perItem = strategy switch
        {
            Strategy.Craft => a.Craft?.Gil,
            Strategy.Gather => a.Gather?.Gil,
            _ => a.Flip?.Gil,
        } ?? 0;
        var share = configuration.MarketSharePercent;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Best strategy: {strategy.ToString().ToLowerInvariant()}");
        ImGui.TextColored(Dim, $"{Gil(perItem)} profit per item x {a.Velocity:0.0} sales/day x {share}% of the sales = {Gil(daily)} per day");
        ImGui.TextColored(
            Dim,
            a.DaysToSellBatch is { } days
                ? $"Selling a batch of {a.BatchSize} takes about {(days < 1 ? "less than a day" : $"{days:0.#} days")} at that share."
                : "No sales recorded, so there is nothing to estimate.");
        ImGui.TextColored(Dim, "Other sellers compete for the same sales; change your expected share in Settings.");
        ImGui.EndTooltip();
    }

    /// <summary>How long ago players last uploaded the market board of your world for this item.</summary>
    private void DrawDataAge(ItemAnalysis a)
    {
        if (a.UploadTime is not { } uploaded)
        {
            ImGui.TextColored(Dim, "?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Universalis has no upload time of your world for this item.");
            return;
        }

        var age = DateTimeOffset.UtcNow - uploaded;
        var freshness = DataFreshness.ClassifyUpload(age);
        ImGui.TextColored(
            FreshnessColor(freshness),
            (freshness == Freshness.Fresh ? string.Empty : "! ") + (age.TotalMinutes < 1 ? "now" : FormatSpan(age)));

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Players last uploaded the market board of your world for this item {FormatAge(age)}.");
        if (freshness != Freshness.Fresh)
        {
            ImGui.TextColored(
                FreshnessColor(freshness),
                freshness == Freshness.VeryStale
                    ? "Over 2 days: prices and sales may be out of date, or listings may no longer exist."
                    : "Over 6 hours: prices may have moved since then.");
        }

        ImGui.EndTooltip();
    }

    // Old prices are drawn in grey so that they do not look as trustworthy as current ones.
    private static void DrawPrice(long? price, Freshness freshness = Freshness.Fresh)
    {
        if (price is not { } p)
            ImGui.TextColored(Dim, "-");
        else if (freshness == Freshness.Fresh)
            ImGui.TextUnformatted(Gil(p));
        else
            ImGui.TextColored(Dim, Gil(p));
    }

    private static void DrawProfit(ProfitResult? profit, Freshness freshness = Freshness.Fresh)
    {
        if (profit is null)
        {
            ImGui.TextColored(Dim, "-");
            return;
        }

        var percent = profit.Percent is { } p ? $"{p:+0;-0;0}%" : "n/a";
        var text = $"{(profit.Gil >= 0 ? "+" : "-")}{Gil(Math.Abs(profit.Gil))} ({percent})";
        ImGui.TextColored(freshness != Freshness.Fresh ? Dim : profit.Gil >= 0 ? Green : Red, text);
    }

    private static Vector4 FreshnessColor(Freshness freshness) => freshness switch
    {
        Freshness.VeryStale => Red,
        Freshness.Stale => Warn,
        _ => Dim,
    };

    /// <summary>The sell price, with a marker when it is far from the recent average sale price.</summary>
    private void DrawSellPrice(ItemAnalysis a, Freshness freshness)
    {
        var deviates = a.PriceDeviationPercent is { } deviation && Math.Abs(deviation) > DeviationWarningPercent;

        ImGui.BeginGroup();
        DrawPrice(a.WorldPrice, freshness);
        if (freshness != Freshness.Fresh)
        {
            ImGui.SameLine();
            ImGui.TextColored(FreshnessColor(freshness), "(old)");
        }

        if (deviates)
        {
            ImGui.SameLine();
            ImGui.TextColored(Warn, "!");
        }

        ImGui.EndGroup();

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Sell price used: {(a.WorldPrice is { } p ? Gil(p) : "-")}  ({BasisName(configuration.SellPriceBasis)})");
        ImGui.TextColored(Dim, $"Lowest listing: {(a.LowestListing is { } l ? Gil(l) : "-")}");
        ImGui.TextColored(Dim, $"Recent average sale: {(a.AverageSale is { } avg ? Gil(avg) : "-")}");

        if (a.PriceDeviationPercent is { } dev && deviates)
        {
            ImGui.TextColored(
                Warn,
                dev < 0
                    ? $"{-dev:0}% below the recent average: lowball listings or a price crash. Check before relying on it."
                    : $"{dev:0}% above the recent average: it may sell slowly at this price.");
        }

        if (a.UploadTime is { } uploaded)
            ImGui.TextColored(Dim, $"Last market board upload on your world: {FormatAge(DateTimeOffset.UtcNow - uploaded)}");

        if (freshness != Freshness.Fresh && planner.DataAge is { } age)
            ImGui.TextColored(FreshnessColor(freshness), $"These prices were loaded {FormatAge(age)}; the last update did not go through.");

        ImGui.EndTooltip();
    }

    /// <summary>A craft/gather profit cell: hover for the breakdown, click for a list that opens the crafting log.</summary>
    private void DrawMaterialsCell(ItemAnalysis a, ProfitResult? profit, IReadOnlyList<CostLine> lines, Freshness freshness, string mode, string title)
    {
        DrawProfit(profit, freshness);
        if (lines.Count == 0)
            return;

        var popupId = $"##materials-{mode}-{a.ItemId}";
        var popupOpen = ImGui.IsPopupOpen(popupId);

        if (ImGui.IsItemClicked())
            ImGui.OpenPopup(popupId);
        else if (ImGui.IsItemHovered() && !popupOpen)
        {
            ImGui.BeginTooltip();
            DrawMaterials(a, lines, title, interactive: false);
            ImGui.TextColored(Dim, "Click to open the list.");
            ImGui.EndTooltip();
        }

        if (!ImGui.BeginPopup(popupId))
            return;

        DrawMaterials(a, lines, title, interactive: true);
        ImGui.EndPopup();
    }

    private void DrawMaterials(ItemAnalysis a, IReadOnlyList<CostLine> lines, string title, bool interactive)
    {
        var data = planner.GameData;

        ImGui.TextColored(Dim, $"{title}");
        ImGui.TextColored(Dim, $"Costs for a batch of {a.BatchSize} items (change it in Settings).");

        foreach (var line in lines)
        {
            var source = line.Source switch
            {
                CostSource.Market => "buy",
                CostSource.Vendor => "vendor",
                CostSource.Gathered => "gather",
                CostSource.Crafted => "craft it",
                _ => "no price",
            };

            var cost = line.Source == CostSource.Unavailable
                ? "?"
                : $"{Gil(line.TotalCost)} gil ({Gil(line.UnitCost)} each)";
            var label = $"{line.TotalUnits}x {line.Name}: {cost}, {source}";

            if (interactive && actions.CanOpenCraftingLog(line.ItemId))
            {
                if (ImGui.Selectable($"{label}##mat{line.ItemId}", false, ImGuiSelectableFlags.DontClosePopups))
                    actions.OpenCraftingLog(line.ItemId);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Open {line.Name} in the crafting log");
            }
            else
            {
                ImGui.TextUnformatted(label);
            }

            foreach (var purchase in line.Purchases.Take(MaxPurchasesShown))
            {
                var where = purchase.WorldId == 0 ? "market" : data.GetWorldName(purchase.WorldId);
                ImGui.TextColored(Dim, $"      {purchase.Quantity}x on {where} at {Gil(purchase.PricePerUnit)}");
            }

            if (line.Purchases.Count > MaxPurchasesShown)
                ImGui.TextColored(Dim, $"      and {line.Purchases.Count - MaxPurchasesShown} more listings");

            if (line.ShortfallUnits > 0)
                ImGui.TextColored(Warn, $"      only {line.TotalUnits - line.ShortfallUnits} listed; the other {line.ShortfallUnits} are estimated at the last listing's price");
        }

        if (interactive)
            ImGui.TextColored(Dim, "Click a craftable material to open it in the crafting log.");
    }

    /// <summary>Right-click menu of an item row. <paramref name="inList"/> is true in the Market table.</summary>
    private void DrawItemContextMenu(uint itemId, bool hq, bool inList)
    {
        if (!ImGui.BeginPopupContextItem($"##ctx{itemId}"))
            return;

        if (!inList)
        {
            var tracked = planner.IsTracked(itemId);
            if (ImGui.MenuItem(tracked ? "Already in Gil Planner" : "Add to Gil Planner", string.Empty, false, !tracked))
                planner.Add(itemId);
            ImGui.Separator();
        }

        if (ImGui.MenuItem("Show item link in chat"))
            actions.LinkInChat(itemId, hq);
        if (ImGui.MenuItem("Open on Universalis"))
            actions.OpenOnUniversalis(itemId);
        if (ImGui.MenuItem("Open in crafting log", string.Empty, false, actions.CanOpenCraftingLog(itemId)))
            actions.OpenCraftingLog(itemId);

        if (inList)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Remove from list"))
                planner.Remove(itemId);
        }

        ImGui.EndPopup();
    }

    private void DrawPopularTab()
    {
        planner.PopularTick();

        var popular = planner.Popular;
        var world = planner.PricingWorld();

        ImGui.BeginDisabled(popular.IsScanning || world is null);
        if (ImGui.Button("Rescan"))
            planner.ScanPopular();
        ImGui.EndDisabled();
        ImGui.SameLine();

        if (world is null)
        {
            ImGui.TextColored(Dim, "Log in to a character to scan the market.");
            return;
        }

        if (popular.IsScanning)
        {
            var (done, total) = popular.Progress;
            ImGui.ProgressBar(total == 0 ? 0f : (float)done / total, new Vector2(220, 0), total == 0 ? "starting..." : $"{done}/{total}");
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"Scanning craftable items on {world.Value.Name}...");
        }
        else if (popular.LastScanned is { } scanned && popular.WorldId == world.Value.Id)
        {
            ImGui.TextColored(Dim, $"{world.Value.Name}: scanned {FormatAge(DateTimeOffset.UtcNow - scanned)}. Craftable items selling at least 1 per day, best sellers first.");
        }

        if (popular.Failure is { } failure)
        {
            ImGui.BeginGroup();
            ImGui.TextColored(
                Red,
                popular.WorldId == world.Value.Id && popular.LastScanned is { } last
                    ? $"! The scan failed: {failure.Message}. Showing the saved results from {FormatAge(DateTimeOffset.UtcNow - last)}, they may be out of date."
                    : $"! The scan failed: {failure.Message}.");
            ImGui.TextColored(Dim, "  Press Rescan to try again.");
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Technical detail: {failure.Detail}");
        }
        else if (!popular.IsScanning && popular.WorldId == world.Value.Id && popular.LastScanned is { } scannedAt
                 && DateTimeOffset.UtcNow - scannedAt > TimeSpan.FromHours(Math.Max(1, configuration.PopularCacheHours) * 2))
        {
            ImGui.TextColored(Warn, $"! These results are {FormatSpan(DateTimeOffset.UtcNow - scannedAt)} old. Press Rescan for current numbers.");
        }

        ImGui.Separator();

        var results = popular.WorldId == world.Value.Id ? popular.Results : [];
        if (results.Count == 0)
        {
            if (!popular.IsScanning)
                ImGui.TextColored(Dim, "No data yet.");
            return;
        }

        DrawPopularTable(results);
    }

    private void DrawPopularTable(IReadOnlyList<PopularItem> results)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##popular", 5, flags, ImGui.GetContentRegionAvail()))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Price (my world)", ImGuiTableColumnFlags.PreferSortDescending, 1.2f);
        ImGui.TableSetupColumn("Sales/day", ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.DefaultSort, 1f);
        ImGui.TableSetupColumn("Gil/day", ImGuiTableColumnFlags.PreferSortDescending, 1.3f);
        ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize, 0.6f);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            popularSortColumn = specs.Specs.ColumnIndex;
            popularSortAscending = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }

        foreach (var item in SortedPopular(results))
        {
            ImGui.TableNextRow();
            DrawPopularRow(item);
        }

        ImGui.EndTable();
    }

    private IEnumerable<PopularItem> SortedPopular(IReadOnlyList<PopularItem> rows)
    {
        if (popularSortColumn == PopItem)
        {
            return popularSortAscending
                ? rows.OrderBy(p => planner.GameData.GetItemName(p.ItemId), StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(p => planner.GameData.GetItemName(p.ItemId), StringComparer.OrdinalIgnoreCase);
        }

        double Key(PopularItem p) => popularSortColumn switch
        {
            PopPrice => p.Price ?? 0,
            PopGilPerDay => p.GilPerDay,
            _ => p.Velocity,
        };

        return popularSortAscending ? rows.OrderBy(Key) : rows.OrderByDescending(Key);
    }

    private void DrawPopularRow(PopularItem item)
    {
        var data = planner.GameData;

        ImGui.TableNextColumn();
        if (data.GetItem(item.ItemId) is { } info)
        {
            var icon = textureProvider.GetFromGameIcon(new GameIconLookup(info.IconId)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new Vector2(24, 24));
            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Selectable(
            $"{data.GetItemName(item.ItemId)}{(item.IsHq ? " (HQ)" : string.Empty)}##pop{item.ItemId}",
            false,
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap);
        DrawItemContextMenu(item.ItemId, item.IsHq, inList: false);

        ImGui.TableNextColumn();
        DrawPrice(item.Price);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(item.Velocity.ToString("N1", CultureInfo.InvariantCulture));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Gil(item.GilPerDay));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Estimated daily turnover on your world: sales per day x average sale price.");

        ImGui.TableNextColumn();
        if (planner.IsTracked(item.ItemId))
            ImGui.TextColored(Dim, "Added");
        else if (ImGui.SmallButton($"Add##pop{item.ItemId}"))
            planner.Add(item.ItemId);
    }

    private void DrawSettingsTab()
    {
        var openOnStartup = configuration.OpenOnStartup;
        if (ImGui.Checkbox("Open this window when the game starts", ref openOnStartup))
        {
            configuration.OpenOnStartup = openOnStartup;
            configuration.Save(pluginInterface);
        }

        ImGui.TextColored(Dim, "You can always open it with /gilplanner or the plugin's button in /xlplugins.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Price updates");

        var autoRefresh = configuration.AutoRefresh;
        if (ImGui.Checkbox("Refresh prices automatically while this window is open", ref autoRefresh))
        {
            configuration.AutoRefresh = autoRefresh;
            configuration.Save(pluginInterface);
        }

        ImGui.BeginDisabled(!configuration.AutoRefresh);
        var minutes = configuration.AutoRefreshMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Refresh every (minutes)", ref minutes, 1, 60))
            configuration.AutoRefreshMinutes = minutes;
        SaveWhenReleased();

        ImGui.EndDisabled();
        ImGui.TextColored(Dim, "Adding an item or pressing Refresh always fetches. The Popular scan only runs when you open that tab or press Rescan.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Pricing");

        ImGui.TextUnformatted("\"My world\" prices are for:");
        DrawWorldOption($"My character's home world{WorldSuffix(planner.HomeWorld())}", true);
        DrawWorldOption($"The world I am on right now{WorldSuffix(planner.CurrentWorld())}", false);
        ImGui.TextColored(Dim, "Applies to sell prices, sales per day, the Popular tab, and which data center is searched for the cheapest prices.");
        ImGui.Spacing();

        var preferHq = configuration.PreferHq;
        if (ImGui.Checkbox("Sell HQ when the item can be HQ", ref preferHq))
        {
            configuration.PreferHq = preferHq;
            configuration.Save(pluginInterface);
        }

        var tax = configuration.MarketTaxPercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Market tax (%)", ref tax, 0f, 10f, "%.1f"))
            configuration.MarketTaxPercent = tax;
        SaveWhenReleased();

        var batch = configuration.BatchSize;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Batch size (items)", ref batch, 1, 99))
            configuration.BatchSize = batch;
        SaveWhenReleased();

        ImGui.TextColored(Dim, "Material costs are for this many finished items. Buying more means working up the listings, so it costs more per unit.");

        var share = configuration.MarketSharePercent;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("My share of the sales (%)", ref share, 1, 100))
            configuration.MarketSharePercent = share;
        SaveWhenReleased();

        ImGui.TextColored(Dim, "Used for Profit/day and the time to sell a batch. Other sellers compete for the same buyers, so you will not sell everything that sells.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Sell price on my world:");
        DrawBasisOption("Robust: median of the cheapest 5 listings (ignores lowballs)", SellPriceBasis.Robust);
        DrawBasisOption("Lowest listing", SellPriceBasis.LowestListing);
        DrawBasisOption("Average sale price", SellPriceBasis.AverageSale);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Popular tab");

        var cacheHours = configuration.PopularCacheHours;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Reuse a scan for (hours)", ref cacheHours, 1, 72))
            configuration.PopularCacheHours = cacheHours;
        SaveWhenReleased();

        ImGui.TextColored(Dim, "Scans are saved to disk, so they survive restarts. An older scan is shown while a new one runs.");
    }

    /// <summary>
    /// Saves the config once a slider has been let go. A slider reports a change on every frame of a drag, and saving
    /// on each of those would rewrite the config file dozens of times per second.
    /// </summary>
    private void SaveWhenReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            configuration.Save(pluginInterface);
    }

    private static string WorldSuffix((uint Id, string Name)? world) => world is { } w ? $" ({w.Name})" : string.Empty;

    private void DrawWorldOption(string label, bool homeWorld)
    {
        if (!ImGui.RadioButton(label, configuration.UseHomeWorld == homeWorld))
            return;

        configuration.UseHomeWorld = homeWorld;
        configuration.Save(pluginInterface);

        // The prices on screen belong to the other world now; load the ones for the newly chosen world.
        planner.Refresh();
    }

    private void DrawBasisOption(string label, SellPriceBasis basis)
    {
        if (!ImGui.RadioButton(label, configuration.SellPriceBasis == basis))
            return;

        configuration.SellPriceBasis = basis;
        configuration.Save(pluginInterface);

        // The robust price needs the listings of your world, which are only fetched when it is selected.
        if (basis == SellPriceBasis.Robust && planner.Market.WorldListings.Count == 0)
            planner.Refresh();
    }

    private static string BasisName(SellPriceBasis basis) => basis switch
    {
        SellPriceBasis.Robust => "median of the cheapest 5 listings",
        SellPriceBasis.AverageSale => "average sale price",
        _ => "lowest listing",
    };

    private static string Gil(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

    private static string Gil(double amount) => Gil((long)Math.Round(amount));

    private static string FormatDuration(TimeSpan duration)
    {
        var seconds = (int)Math.Ceiling(Math.Max(0, duration.TotalSeconds));
        return seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";
    }

    private static string FormatAge(TimeSpan age) => age.TotalMinutes < 1 ? "just now" : $"{FormatSpan(age)} ago";

    /// <summary>A rough length of time without "ago", e.g. "23 min", for sentences like "23 min old".</summary>
    private static string FormatSpan(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "under a minute",
        < 60 => $"{(int)span.TotalMinutes} min",
        < 60 * 48 => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalDays} d",
    };
}
