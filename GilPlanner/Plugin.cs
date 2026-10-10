using System.IO;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using GilPlanner.Pricing;
using GilPlanner.Windows;

namespace GilPlanner;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gilplanner";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static INotificationManager NotificationManager { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;

    private readonly WindowSystem windowSystem = new("GilPlanner");
    private readonly Configuration configuration;
    private readonly MainWindow mainWindow;
    private readonly GameData gameData;
    private readonly UniversalisClient universalis;
    private readonly MarketService marketService;
    private readonly PopularService popularService;
    private readonly Planner planner;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        gameData = new GameData(DataManager);
        universalis = new UniversalisClient($"GilPlanner-Dalamud/{GetType().Assembly.GetName().Version}");
        marketService = new MarketService(universalis, gameData);
        var popularCache = new PopularCache(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "popular-cache.json"));
        popularService = new PopularService(universalis, popularCache.Store);
        planner = new Planner(configuration, PluginInterface, PlayerState, gameData, marketService, popularService, popularCache);

        mainWindow = new MainWindow(
            configuration,
            PluginInterface,
            TextureProvider,
            planner,
            new ItemActions(gameData, ChatGui, NotificationManager));
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Gil Planner window.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.OpenSettings;
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Framework.Update += OnFrameworkUpdate;

        Log.Information("Gil Planner plugin loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.OpenSettings;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        Framework.Update -= OnFrameworkUpdate;

        CommandManager.RemoveHandler(CommandName);

        windowSystem.RemoveAllWindows();

        marketService.Dispose();
        popularService.Dispose();
        universalis.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework) => planner.Update();

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory { TargetItem: { } item })
            return;

        // Strip the HQ / collectable offsets so we get the plain item id.
        var itemId = ItemUtil.GetBaseId(item.ItemId).ItemId;
        if (!gameData.IsCraftableAndMarketable(itemId))
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = planner.IsTracked(itemId) ? "Already in Gil Planner" : "Add to Gil Planner",
            IsEnabled = !planner.IsTracked(itemId),
            Prefix = SeIconChar.BoxedLetterG,
            PrefixColor = 34,
            OnClicked = _ => AddItem(itemId),
        });
    }

    private void AddItem(uint itemId)
    {
        planner.Add(itemId);
        NotificationManager.AddNotification(new Notification
        {
            Content = $"Added {gameData.GetItemName(itemId)} to Gil Planner.",
            Type = NotificationType.Success,
        });
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();
}
