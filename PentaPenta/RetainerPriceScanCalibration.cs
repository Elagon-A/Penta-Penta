using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace PentaPenta;

internal sealed class RetainerPriceScanCalibration : IDisposable
{
    private readonly Services services;
    private readonly List<string> events = [];

    internal bool IsArmed { get; private set; }
    internal string Status { get; private set; } = "Calibration has not been run.";

    internal RetainerPriceScanCalibration(Services services)
    {
        this.services = services;
        services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "RetainerSellList", OnSellListEvent);
        services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "RetainerSell", OnSellEvent);
        services.Framework.Update += OnFrameworkUpdate;
    }

    internal void Arm()
    {
        events.Clear();
        IsArmed = true;
        Status = "ARMED: manually select one retainer listing, then click Compare Prices in Adjust Price.";
    }

    private void OnSellListEvent(AddonEvent _, AddonArgs args) => Record("RetainerSellList", args);
    private void OnSellEvent(AddonEvent _, AddonArgs args) => Record("RetainerSell", args);

    private void Record(string addon, AddonArgs args)
    {
        if (!IsArmed || args is not AddonReceiveEventArgs receive) return;
        var entry = $"{addon}: type={receive.AtkEventType}, param={receive.EventParam}";
        if (events.Count == 0 || events[^1] != entry) events.Add(entry);
        Status = $"Captured {events.Count} event(s). Continue until Search Results opens.";
        services.Log.Information("[RetainerPriceCalibration] {Entry}", entry);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsArmed || services.GameGui.GetAddonByName("ItemSearchResult").IsNull) return;
        IsArmed = false;
        Status = events.Count == 0
            ? "Search Results opened, but no RetainerSell events were captured."
            : "CALIBRATION COMPLETE: " + string.Join(" | ", events);
        services.Log.Information("[RetainerPriceCalibration] {Status}", Status);
    }

    public void Dispose()
    {
        services.Framework.Update -= OnFrameworkUpdate;
        services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "RetainerSellList", OnSellListEvent);
        services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "RetainerSell", OnSellEvent);
    }
}
