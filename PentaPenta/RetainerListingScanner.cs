using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace PentaPenta;

internal sealed class RetainerListingScanner(Services services)
{
    internal unsafe RetainerListingCapture Capture(IReadOnlyList<PentameldPricingWatchItem> watchList)
    {
        if (services.GameGui.GetAddonByName("RetainerSellList").IsNull)
            return new RetainerListingCapture([], 0, "Open a retainer's Items for Sale window before capturing.");

        var manager = InventoryManager.Instance();
        if (manager == null)
            return new RetainerListingCapture([], 0, "The inventory manager was not available.");

        var watched = watchList
            .GroupBy(x => (x.ItemId, x.Hq))
            .ToDictionary(x => x.Key, x => x.First());
        var listings = new List<CapturedRetainerListing>();
        var loadedCount = 0;
        foreach (ref readonly var slot in services.Inventory.GetInventoryItems(GameInventoryType.RetainerMarket))
        {
            if (slot.IsEmpty) continue;
            loadedCount++;
            if (!watched.TryGetValue((slot.BaseItemId, slot.IsHq), out var item)) continue;
            var materiaCount = slot.MateriaEntries.Count(x => x.Type.RowId != 0);
            if (materiaCount != 5) continue;
            var currentPrice = manager->GetRetainerMarketPrice((short)slot.InventorySlot);
            listings.Add(new CapturedRetainerListing(
                slot.BaseItemId, item.Name, slot.IsHq, slot.InventorySlot, materiaCount, currentPrice));
        }

        var status = $"Captured {listings.Count} watched pentamelded listing(s) from {loadedCount} loaded sale slot(s).";
        return new RetainerListingCapture(listings, loadedCount, status);
    }
}

internal sealed record CapturedRetainerListing(
    uint ItemId,
    string Name,
    bool Hq,
    uint MarketSlot,
    int MateriaCount,
    ulong CurrentPrice);

internal sealed record RetainerListingCapture(
    IReadOnlyList<CapturedRetainerListing> Listings,
    int LoadedListings,
    string Status);
