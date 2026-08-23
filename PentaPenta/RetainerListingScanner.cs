using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace PentaPenta;

internal sealed class RetainerListingScanner(Services services)
{
    internal unsafe RetainerListingCapture Capture(IReadOnlyList<PentameldPricingWatchItem> watchList)
        => CaptureLoaded(watchList, true);

    internal unsafe RetainerListingCapture CaptureLoadedActiveRetainer(IReadOnlyList<PentameldPricingWatchItem> watchList)
        => CaptureLoaded(watchList, false);

    private unsafe RetainerListingCapture CaptureLoaded(IReadOnlyList<PentameldPricingWatchItem> watchList, bool requireSaleWindow)
    {
        if (requireSaleWindow && services.GameGui.GetAddonByName("RetainerSellList").IsNull)
            return new RetainerListingCapture("", [], 0, "Open a retainer's Items for Sale window before capturing.");

        var manager = InventoryManager.Instance();
        if (manager == null)
            return new RetainerListingCapture("", [], 0, "The inventory manager was not available.");
        var retainerManager = RetainerManager.Instance();
        var activeRetainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
        var retainerName = activeRetainer == null ? "" : activeRetainer->NameString;
        if (retainerName.Length == 0)
            return new RetainerListingCapture("", [], 0, "The active retainer identity was not available.");

        var watched = watchList
            .GroupBy(x => (x.ItemId, x.Hq))
            .ToDictionary(x => x.Key, x => x.First());
        var listings = new List<CapturedRetainerListing>();
        var loadedCount = 0;
        var rowIndex = 0;
        foreach (ref readonly var slot in services.Inventory.GetInventoryItems(GameInventoryType.RetainerMarket))
        {
            if (slot.IsEmpty) continue;
            loadedCount++;
            var currentRow = rowIndex++;
            if (!watched.TryGetValue((slot.BaseItemId, slot.IsHq), out var item)) continue;
            var materiaCount = slot.MateriaEntries.Count(x => x.Type.RowId != 0);
            if (materiaCount != 5) continue;
            var currentPrice = manager->GetRetainerMarketPrice((short)slot.InventorySlot);
            listings.Add(new CapturedRetainerListing(
                slot.BaseItemId, item.Name, slot.IsHq, slot.InventorySlot, currentRow, materiaCount, currentPrice));
        }

        var status = $"Captured {listings.Count} watched pentamelded listing(s) from {loadedCount} loaded sale slot(s) on {retainerName}.";
        return new RetainerListingCapture(retainerName, listings, loadedCount, status);
    }

    internal unsafe PriceChangeSubmission SubmitOne(CapturedRetainerListing captured, uint proposedPrice, int maxDecreasePercent)
    {
        if (services.GameGui.GetAddonByName("RetainerSellList").IsNull)
            return new(false, "The retainer Items for Sale window is no longer open.");
        if (proposedPrice == 0 || proposedPrice > 999_999_999)
            return new(false, "The proposed price is outside the game's valid range.");

        GameInventoryItem live = default;
        foreach (ref readonly var candidate in services.Inventory.GetInventoryItems(GameInventoryType.RetainerMarket))
            if (!candidate.IsEmpty && candidate.InventorySlot == captured.MarketSlot) { live = candidate; break; }
        if (live.IsEmpty || live.BaseItemId != captured.ItemId || live.IsHq != captured.Hq)
            return new(false, "The item identity or quality in that market slot changed after capture.");
        if (live.MateriaEntries.Count(x => x.Type.RowId != 0) != 5)
            return new(false, "The selected listing is no longer verified as 5/5 melded.");

        var manager = InventoryManager.Instance();
        if (manager == null) return new(false, "The inventory manager was not available.");
        var currentPrice = manager->GetRetainerMarketPrice((short)captured.MarketSlot);
        if (currentPrice != captured.CurrentPrice)
            return new(false, $"The live price changed from {captured.CurrentPrice:N0} to {currentPrice:N0} gil; capture again.");
        if (currentPrice == proposedPrice)
            return new(false, "The listing is already at the proposed price.");

        maxDecreasePercent = Math.Clamp(maxDecreasePercent, 0, 100);
        var minimumAllowed = currentPrice * (ulong)(100 - maxDecreasePercent) / 100;
        if (proposedPrice < minimumAllowed)
            return new(false, $"Rejected: {proposedPrice:N0} gil exceeds the {maxDecreasePercent}% maximum decrease.");

        manager->SetRetainerMarketPrice((short)captured.MarketSlot, proposedPrice);
        return new(true, $"Submitted {captured.Name}: {currentPrice:N0} → {proposedPrice:N0} gil.");
    }

    internal unsafe ulong? ReadPrice(CapturedRetainerListing captured)
    {
        if (services.GameGui.GetAddonByName("RetainerSellList").IsNull) return null;
        GameInventoryItem live = default;
        foreach (ref readonly var candidate in services.Inventory.GetInventoryItems(GameInventoryType.RetainerMarket))
            if (!candidate.IsEmpty && candidate.InventorySlot == captured.MarketSlot) { live = candidate; break; }
        if (live.IsEmpty || live.BaseItemId != captured.ItemId || live.IsHq != captured.Hq) return null;
        var manager = InventoryManager.Instance();
        return manager == null ? null : manager->GetRetainerMarketPrice((short)captured.MarketSlot);
    }
}

internal sealed record CapturedRetainerListing(
    uint ItemId,
    string Name,
    bool Hq,
    uint MarketSlot,
    int RowIndex,
    int MateriaCount,
    ulong CurrentPrice);

internal sealed record RetainerListingCapture(
    string RetainerName,
    IReadOnlyList<CapturedRetainerListing> Listings,
    int LoadedListings,
    string Status);

internal sealed record PriceChangeSubmission(bool Submitted, string Status);
