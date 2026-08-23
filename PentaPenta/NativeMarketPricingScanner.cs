using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PentaPenta;

internal sealed class NativeMarketPricingScanner(Services services)
{
    internal unsafe NativeMarketPricingCapture Capture(
        IReadOnlyList<PentameldPricingWatchItem> watchList,
        IReadOnlySet<string> ownRetainers,
        int undercutGil)
    {
        if (services.GameGui.GetAddonByName("ItemSearchResult").IsNull)
            return new([], "Open an item's native Search Results window before capturing.");

        var infoModule = InfoModule.Instance();
        var proxy = infoModule == null ? null : infoModule->GetInfoProxyItemSearch();
        if (proxy == null)
            return new([], "The native marketboard information service was unavailable.");
        if (proxy->SearchItemId == 0)
            return new([], $"The native result item ID was not ready (listings={proxy->ListingCount}, waiting={proxy->WaitingForListings}).");
        if (proxy->ListingCount == 0)
            return new([], $"The native result list is empty for item {proxy->SearchItemId} (waiting={proxy->WaitingForListings}).");

        var watched = watchList.Where(x => x.ItemId == proxy->SearchItemId).ToList();
        if (watched.Count == 0)
            return new([], $"Item {proxy->SearchItemId} is not in the pricing watchlist.");

        var listings = proxy->Listings.ToArray();
        var results = new List<PentameldPriceResult>();
        foreach (var watch in watched)
        {
            var matches = listings.Where(x => x.ItemId == watch.ItemId
                    && x.IsHqItem == watch.Hq
                    && x.UnitPrice > 0
                    && x.MateriaCount == 5
                    && !ownRetainers.Contains(x.CharacterName.ToString()))
                .ToList();
            var cheapest = matches.Count == 0 ? (int?)null : checked((int)matches.Min(x => x.UnitPrice));
            int? proposed = cheapest is null ? null : Math.Max(1, cheapest.Value - Math.Max(0, undercutGil));
            results.Add(new PentameldPriceResult(watch.ItemId, watch.Name, watch.Hq, matches.Count, cheapest, proposed, null));
        }

        var matched = results.Sum(x => x.QualifyingListings);
        return new(results, $"Native capture: {listings.Length} listing(s), {matched} qualifying five-materia match(es) for {watched[0].Name}.");
    }
}

internal sealed record NativeMarketPricingCapture(
    IReadOnlyList<PentameldPriceResult> Results,
    string Status);
