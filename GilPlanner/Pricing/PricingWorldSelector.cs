namespace GilPlanner.Pricing;

/// <summary>Decides which world the prices of the plugin are for: your character's home world or the world you are on.</summary>
public static class PricingWorldSelector
{
    /// <summary>
    /// The chosen world's id, or null when neither is known. If the preferred world is unknown (id 0) the other one is
    /// used, so there are still prices to show.
    /// </summary>
    public static uint? Select(bool useHomeWorld, uint homeWorldId, uint currentWorldId)
    {
        var preferred = useHomeWorld ? homeWorldId : currentWorldId;
        var fallback = useHomeWorld ? currentWorldId : homeWorldId;

        if (preferred != 0)
            return preferred;

        return fallback != 0 ? fallback : null;
    }
}
