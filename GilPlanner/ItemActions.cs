using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GilPlanner;

/// <summary>Things you can do with an item from the plugin's tables. Must be called from the game's main thread.</summary>
public sealed unsafe class ItemActions(GameData gameData, IChatGui chatGui, INotificationManager notifications)
{
    public bool CanOpenCraftingLog(uint itemId) => gameData.GetRecipes(itemId).Count > 0;

    /// <summary>Opens the crafting log on the recipe that makes <paramref name="itemId"/>.</summary>
    public void OpenCraftingLog(uint itemId)
    {
        var recipes = gameData.GetRecipes(itemId);
        if (recipes.Count == 0)
        {
            Notify($"{gameData.GetItemName(itemId)} cannot be crafted.", NotificationType.Warning);
            return;
        }

        var agent = AgentRecipeNote.Instance();
        if (agent is null)
        {
            Notify("The crafting log is not available right now.", NotificationType.Warning);
            return;
        }

        agent->OpenRecipeByRecipeId(recipes[0].RecipeId);
    }

    /// <summary>Prints a clickable item link in your chat log (only you see it).</summary>
    public void LinkInChat(uint itemId, bool hq) =>
        chatGui.Print(new SeStringBuilder().AddText("[Gil Planner] ").AddItemLink(itemId, hq).Build());

    public void OpenOnUniversalis(uint itemId) => Util.OpenLink($"https://universalis.app/market/{itemId}");

    private void Notify(string message, NotificationType type) =>
        notifications.AddNotification(new Notification { Content = message, Type = type });
}
