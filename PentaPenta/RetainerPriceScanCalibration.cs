using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PentaPenta;

internal sealed class RetainerPriceScanCalibration : IDisposable
{
    private readonly Services services;
    private readonly List<string> events = [];
    private readonly List<string> samples = [];
    private int sampleNumber;
    private bool waitingForWindowsToClose;

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
        samples.Clear();
        sampleNumber = 1;
        waitingForWindowsToClose = false;
        IsArmed = true;
        Status = "CALIBRATION 1/2: click sale-list row 1, then click Compare Prices in Adjust Price.";
    }

    internal void Cancel()
    {
        IsArmed = false;
        waitingForWindowsToClose = false;
        Status = $"Calibration stopped after {samples.Count}/2 sample(s).";
    }

    private void OnSellListEvent(AddonEvent _, AddonArgs args) => Record("RetainerSellList", args);
    private void OnSellEvent(AddonEvent _, AddonArgs args) => Record("RetainerSell", args);

    private void Record(string addon, AddonArgs args)
    {
        if (!IsArmed || waitingForWindowsToClose || args is not AddonReceiveEventArgs receive) return;
        var eventName = receive.AtkEventType.ToString();
        if (eventName.Contains("RollOver", StringComparison.Ordinal)
            || eventName.Contains("RollOut", StringComparison.Ordinal)
            || eventName is "MouseOver" or "MouseOut") return;
        var entry = $"{addon}: type={receive.AtkEventType}, param={receive.EventParam}";
        if (events.Count == 0 || events[^1] != entry) events.Add(entry);
        Status = $"CALIBRATION {sampleNumber}/2: captured {events.Count} relevant event(s); continue until Search Results opens.";
        services.Log.Information("[RetainerPriceCalibration] {Entry}", entry);
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!IsArmed) return;
        if (!waitingForWindowsToClose && !services.GameGui.GetAddonByName("ItemSearchResult").IsNull)
        {
            var adjust = services.GameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
            var itemName = adjust == null || adjust->ItemName == null
                ? "unknown item"
                : adjust->ItemName->NodeText.ToString();
            var eventSummary = events.Count == 0 ? "no relevant events" : string.Join(" | ", events);
            var sample = $"row {(sampleNumber == 1 ? 1 : 3)} opened '{itemName}': {eventSummary}";
            samples.Add(sample);
            services.Log.Information("[RetainerPriceCalibration] SAMPLE {SampleNumber}: {Sample}", sampleNumber, sample);
            waitingForWindowsToClose = true;
            Status = sampleNumber == 1
                ? "Sample 1/2 captured. Close Search Results and Adjust Price; then calibration will request row 3."
                : "Sample 2/2 captured. Close Search Results and Adjust Price to finish.";
            return;
        }

        if (!waitingForWindowsToClose
            || !services.GameGui.GetAddonByName("ItemSearchResult").IsNull
            || !services.GameGui.GetAddonByName("RetainerSell").IsNull) return;

        waitingForWindowsToClose = false;
        if (sampleNumber == 1)
        {
            sampleNumber = 2;
            events.Clear();
            Status = "CALIBRATION 2/2: click sale-list row 3, then click Compare Prices in Adjust Price.";
            return;
        }

        IsArmed = false;
        Status = "CALIBRATION COMPLETE: " + string.Join(" || ", samples);
        services.Log.Information("[RetainerPriceCalibration] {Status}", Status);
    }

    public void Dispose()
    {
        services.Framework.Update -= OnFrameworkUpdate;
        services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "RetainerSellList", OnSellListEvent);
        services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "RetainerSell", OnSellEvent);
    }
}
