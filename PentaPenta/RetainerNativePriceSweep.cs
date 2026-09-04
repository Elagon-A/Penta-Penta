using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    private CapturedRetainerListing? rowOpenTestListing;

    internal bool IsRunning => phase != SweepPhase.Idle;
    internal bool IsRowOpenTestArmed { get; private set; }
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
        Status = "Native retainer row sweep is disabled for safety. Use the guided market audit instead.";
        return;
#pragma warning disable CS0162
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
#pragma warning restore CS0162
    }

    internal void Stop(string reason = "Stopped by user.")
    {
        if (phase == SweepPhase.VerifyRowOpenTest)
        {
            phase = SweepPhase.Idle;
            rowOpenTestListing = null;
            Status = $"Row-open test stopped: {reason}";
            return;
        }
        phase = SweepPhase.Idle;
        Status = $"Native scan stopped at {index}/{listings.Count}: {reason}";
    }

    internal void ArmRowOpenTest()
    {
        if (IsRunning)
        {
            Status = "Stop the current native operation before arming a row-open test.";
            return;
        }
        IsRowOpenTestArmed = true;
        Status = "ARMED: the next test will open one verified watched row and stop at Adjust Price.";
    }

    internal void CancelRowOpenTest()
    {
        IsRowOpenTestArmed = false;
        Status = "Row-open test disarmed.";
    }

    internal unsafe void RunRowOpenTest(RetainerListingCapture capture)
    {
        if (!IsRowOpenTestArmed)
        {
            Status = "Arm the one-row test first.";
            return;
        }
        IsRowOpenTestArmed = false;
        if (services.GameGui.GetAddonByName("RetainerSellList").IsNull)
        {
            Status = "Open the retainer Items for Sale window before running the test.";
            return;
        }
        if (!services.GameGui.GetAddonByName("RetainerSell").IsNull)
        {
            Status = "Close the existing Adjust Price window before running the test.";
            return;
        }
        var listing = capture.Listings.OrderBy(x => x.RowIndex).FirstOrDefault();
        if (listing is null)
        {
            Status = "The active retainer has no watched 5/5 listing to test.";
            return;
        }

        var addon = services.GameGui.GetAddonByName<AtkUnitBase>("RetainerSellList");
        if (addon == null || !addon->IsReady)
        {
            Status = "The retainer sale list was not ready.";
            return;
        }
        if (listing.RowIndex < 0 || listing.RowIndex >= capture.LoadedListings)
        {
            Status = $"Displayed row {listing.RowIndex + 1} is outside the captured sale-list range; nothing was invoked.";
            return;
        }

        rowOpenTestListing = listing;
        listings = [listing];
        index = 0;
        phase = SweepPhase.VerifyRowOpenTest;
        rowReadyDeadline = DateTime.UtcNow.AddSeconds(8);
        nextAction = DateTime.UtcNow.AddMilliseconds(300);
        Status = $"Opening row {listing.RowIndex + 1} for {listing.Name}; no Compare Prices action will be sent.";
        // RetainerSellList accepts (command 0, zero-based sale-slot index, 1)
        // to open that listing. This avoids guessing at virtualized renderers and
        // sends exactly one addon callback; Compare Prices is deliberately not sent.
        FireIntCallback(addon, 0, listing.RowIndex, 1);
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
            case SweepPhase.VerifyRowOpenTest:
            {
                var expected = rowOpenTestListing;
                var addon = services.GameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
                if (addon == null || !addon->IsReady)
                {
                    if (DateTime.UtcNow < rowReadyDeadline)
                    {
                        nextAction = DateTime.UtcNow.AddMilliseconds(250);
                        return;
                    }
                    Stop("Adjust Price did not open within 8 seconds.");
                    return;
                }
                var openedName = addon->ItemName == null ? string.Empty : addon->ItemName->NodeText.ToString();
                phase = SweepPhase.Idle;
                rowOpenTestListing = null;
                if (expected is not null && NamesMatch(openedName, expected.Name))
                    Status = $"ROW TEST PASSED: row {expected.RowIndex + 1} opened {expected.Name}. Adjust Price was left open; Compare Prices was not invoked.";
                else
                {
                    ((AtkUnitBase*)addon)->Close(true);
                    Status = $"ROW TEST FAILED: opened '{openedName}', expected '{expected?.Name}'. Adjust Price was closed and no market request was sent.";
                }
                return;
            }
            case SweepPhase.SelectRow:
            {
                var addon = services.GameGui.GetAddonByName<AtkUnitBase>("RetainerSellList");
                if (addon == null || !addon->IsReady) { Stop("Items for Sale closed or was not ready."); return; }
                var list = FindPrimaryList(addon, out var detectedLists, out var largestCount);
                if (list != null && listing.RowIndex >= 0 && listing.RowIndex < list->GetItemCount())
                {
                    list->SelectItem(listing.RowIndex, true);
                    list->DispatchItemEvent(listing.RowIndex, AtkEventType.ListItemClick);
                }
                else
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
                phase = SweepPhase.OpenCompare;
                nextAction = DateTime.UtcNow.AddSeconds(1);
                Status = $"Opening Adjust Price for {listing.Name} ({index + 1}/{listings.Count})...";
                break;
            }
            case SweepPhase.OpenCompare:
            {
                var addon = services.GameGui.GetAddonByName<AddonRetainerSell>("RetainerSell");
                if (addon == null || !addon->IsReady) { Stop($"Adjust Price did not open for {listing.Name}."); return; }
                var openedName = addon->ItemName == null ? string.Empty : addon->ItemName->NodeText.ToString();
                if (!NamesMatch(openedName, listing.Name))
                {
                    Stop($"Safety stop: row {listing.RowIndex + 1} opened '{openedName}', expected '{listing.Name}'. No market search was sent.");
                    return;
                }
                if (addon->ComparePrices == null || !addon->ComparePrices->IsEnabled)
                {
                    Stop($"Compare Prices was not available for {listing.Name}.");
                    return;
                }
                if (!InvokeTypedButtonEvent((AtkUnitBase*)addon, addon->ComparePrices, 4))
                {
                    Stop("The typed Compare Prices button event was not found.");
                    return;
                }
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

    private static bool NamesMatch(string opened, string expected)
        => NormalizeName(opened).Equals(NormalizeName(expected), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value)
        => value.Replace("★", string.Empty, StringComparison.Ordinal).Trim();

    private static unsafe AtkComponentList* FindPrimaryList(AtkUnitBase* addon, out int detectedLists, out int largestCount)
    {
        AtkComponentList* best = null;
        var bestCount = -1;
        detectedLists = 0;
        var visited = new HashSet<nint>();
        FindListsRecursive(&addon->UldManager, 0, visited, ref best, ref bestCount, ref detectedLists);
        largestCount = bestCount;
        return best;
    }

    private static unsafe void FireIntCallback(AtkUnitBase* addon, params int[] values)
    {
        var args = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            args[i].Type = AtkValueType.Int;
            args[i].Int = values[i];
        }
        addon->FireCallback((uint)values.Length, args, true);
    }

    private static unsafe void FindListsRecursive(
        AtkUldManager* manager,
        int depth,
        HashSet<nint> visited,
        ref AtkComponentList* best,
        ref int bestCount,
        ref int detectedLists)
    {
        if (manager == null || manager->NodeList == null || depth > 8 || !visited.Add((nint)manager)) return;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || node->Type != NodeType.Component) continue;
            var component = node->GetAsAtkComponentNode()->Component;
            if (component == null) continue;
            var type = component->GetComponentType();
            if (type == ComponentType.List || type == ComponentType.TreeList)
            {
                detectedLists++;
                var list = (AtkComponentList*)component;
                var count = list->GetItemCount();
                if (count > bestCount)
                {
                    best = list;
                    bestCount = count;
                }
            }
            FindListsRecursive(&component->UldManager, depth + 1, visited, ref best, ref bestCount, ref detectedLists);
        }
    }

    private static unsafe bool InvokeTypedButtonEvent(AtkUnitBase* addon, AtkComponentButton* button, uint expectedParam)
    {
        var ownerNode = button->OwnerNode;
        var evt = ownerNode == null ? null : ownerNode->AtkResNode.AtkEventManager.Event;
        while (evt != null)
        {
            if (evt->State.EventType == AtkEventType.ButtonClick && evt->Param == expectedParam)
            {
                addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
                return true;
            }
            evt = evt->NextEvent;
        }
        return InvokeButtonEvent(addon, expectedParam);
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

    private enum SweepPhase { Idle, VerifyRowOpenTest, SelectRow, OpenCompare, WaitResults }
}
