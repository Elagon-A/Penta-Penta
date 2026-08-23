using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PentaPenta;

internal sealed class RetainerNativePriceSweep : IDisposable
{
    private readonly Services services;
    private readonly NativeMarketPricingScanner nativeScanner;
    private List<CapturedRetainerListing> listings = [];
    private IReadOnlyList<PentameldPricingWatchItem> watchList = [];
    private IReadOnlySet<string> ownRetainers = new HashSet<string>();
    private int undercutGil;
    private int index;
    private DateTime nextAction;
    private DateTime rowReadyDeadline;
    private SweepPhase phase;

    internal bool IsRunning => phase != SweepPhase.Idle;
    internal string Status { get; private set; } = "Native retainer scan has not been run.";
    internal event Action<NativeMarketPricingCapture>? Captured;

    internal RetainerNativePriceSweep(Services services, NativeMarketPricingScanner nativeScanner)
    {
        this.services = services;
        this.nativeScanner = nativeScanner;
        services.Framework.Update += OnFrameworkUpdate;
    }

    internal void Start(
        RetainerListingCapture capture,
        IReadOnlyList<PentameldPricingWatchItem> watched,
        IReadOnlySet<string> exclusions,
        int undercut)
    {
        if (services.GameGui.GetAddonByName("RetainerSellList").IsNull)
        {
            Status = "Open the retainer Items for Sale window before starting.";
            return;
        }
        listings = capture.Listings.ToList();
        if (listings.Count == 0)
        {
            Status = "The captured retainer has no watched 5/5 listings to scan.";
            return;
        }
        watchList = watched;
        ownRetainers = exclusions;
        undercutGil = undercut;
        index = 0;
        phase = SweepPhase.SelectRow;
        nextAction = DateTime.UtcNow.AddSeconds(1);
        rowReadyDeadline = DateTime.UtcNow.AddSeconds(8);
        Status = $"Native scan started for {capture.RetainerName}: 0/{listings.Count}. Do not interact with the retainer or market windows.";
    }

    internal void Stop(string reason = "Stopped by user.")
    {
        phase = SweepPhase.Idle;
        Status = $"Native scan stopped at {index}/{listings.Count}: {reason}";
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning || DateTime.UtcNow < nextAction) return;
        if (index >= listings.Count)
        {
            phase = SweepPhase.Idle;
            Status = $"NATIVE SCAN COMPLETE: captured {listings.Count}/{listings.Count} watched listing item(s).";
            return;
        }

        var listing = listings[index];
        switch (phase)
        {
            case SweepPhase.SelectRow:
            {
                var addon = services.GameGui.GetAddonByName<AtkUnitBase>("RetainerSellList");
                if (addon == null || !addon->IsReady) { Stop("Items for Sale closed or was not ready."); return; }
                var list = FindPrimaryList(addon, out var detectedLists, out var largestCount);
                if (list == null || listing.RowIndex < 0 || listing.RowIndex >= list->GetItemCount())
                {
                    if (DateTime.UtcNow < rowReadyDeadline)
                    {
                        Status = $"Waiting for retainer list row {listing.RowIndex + 1} ({detectedLists} list component(s), largest count {largestCount})...";
                        nextAction = DateTime.UtcNow.AddMilliseconds(500);
                        return;
                    }
                    Stop($"Could not verify list row {listing.RowIndex + 1} for {listing.Name}; detected {detectedLists} list component(s), largest count {largestCount}.");
                    return;
                }
                list->SelectItem(listing.RowIndex, true);
                list->DispatchItemEvent(listing.RowIndex, AtkEventType.ListItemClick);
                phase = SweepPhase.OpenCompare;
                nextAction = DateTime.UtcNow.AddSeconds(1);
                Status = $"Opening Adjust Price for {listing.Name} ({index + 1}/{listings.Count})...";
                break;
            }
            case SweepPhase.OpenCompare:
            {
                var addon = services.GameGui.GetAddonByName<AtkUnitBase>("RetainerSell");
                if (addon == null || !addon->IsReady) { Stop($"Adjust Price did not open for {listing.Name}."); return; }
                if (!InvokeButtonEvent(addon, 4)) { Stop("The calibrated Compare Prices button event was not found."); return; }
                phase = SweepPhase.WaitResults;
                nextAction = DateTime.UtcNow.AddMilliseconds(750);
                Status = $"Waiting for market results for {listing.Name} ({index + 1}/{listings.Count})...";
                break;
            }
            case SweepPhase.WaitResults:
            {
                if (services.GameGui.GetAddonByName("ItemSearchResult").IsNull)
                {
                    nextAction = DateTime.UtcNow.AddMilliseconds(500);
                    return;
                }
                var capture = nativeScanner.Capture(watchList, ownRetainers, undercutGil);
                if (capture.Results.Count == 0) { Stop(capture.Status); return; }
                if (capture.Results.Any(x => x.ItemId != listing.ItemId))
                {
                    Stop($"Native results did not match expected item {listing.Name}.");
                    return;
                }
                Captured?.Invoke(capture);
                var resultsAddon = services.GameGui.GetAddonByName<AtkUnitBase>("ItemSearchResult");
                if (resultsAddon != null) resultsAddon->Close(true);
                var adjustAddon = services.GameGui.GetAddonByName<AtkUnitBase>("RetainerSell");
                if (adjustAddon != null) adjustAddon->Close(true);
                index++;
                phase = SweepPhase.SelectRow;
                nextAction = DateTime.UtcNow.AddSeconds(2);
                rowReadyDeadline = DateTime.UtcNow.AddSeconds(8);
                Status = $"Captured {listing.Name}; {index}/{listings.Count}.";
                break;
            }
        }
    }

    private static unsafe AtkComponentList* FindPrimaryList(AtkUnitBase* addon, out int detectedLists, out int largestCount)
    {
        AtkComponentList* best = null;
        var bestCount = -1;
        detectedLists = 0;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Component) continue;
            var component = node->GetAsAtkComponentNode()->Component;
            if (component == null) continue;
            var type = component->GetComponentType();
            // This addon has used both variants across UI revisions. TreeList derives
            // from AtkComponentList, so its list base can use the same selection API.
            if (type != ComponentType.List && type != ComponentType.TreeList) continue;
            detectedLists++;
            var list = (AtkComponentList*)component;
            var count = list->GetItemCount();
            if (count <= bestCount) continue;
            best = list;
            bestCount = count;
        }
        largestCount = bestCount;
        return best;
    }

    private static unsafe bool InvokeButtonEvent(AtkUnitBase* addon, uint expectedParam)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Component) continue;
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode->Component;
            if (component == null || component->GetComponentType() != ComponentType.Button) continue;
            var evt = componentNode->AtkResNode.AtkEventManager.Event;
            while (evt != null)
            {
                if (evt->State.EventType == AtkEventType.ButtonClick && evt->Param == expectedParam)
                {
                    addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
                    return true;
                }
                evt = evt->NextEvent;
            }
        }
        return false;
    }

    public void Dispose()
    {
        services.Framework.Update -= OnFrameworkUpdate;
        if (IsRunning) Stop("Plugin unloaded.");
    }

    private enum SweepPhase { Idle, SelectRow, OpenCompare, WaitResults }
}
