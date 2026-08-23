using Dalamud.Game.Network.Structures;

namespace PentaPenta;

internal sealed class MarketBoardReceiveDiagnostic : IDisposable
{
    private readonly Services services;
    private readonly object sync = new();
    private MarketBoardReceiveSnapshot? latest;

    internal MarketBoardReceiveSnapshot? Latest
    {
        get { lock (sync) return latest; }
    }

    internal MarketBoardReceiveDiagnostic(Services services)
    {
        this.services = services;
        services.MarketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        var listings = offerings.ItemListings;
        var itemId = listings.Count == 0 ? 0u : listings[0].ItemId;
        var withMateria = listings.Count(x => x.MateriaCount > 0);
        var fiveMateria = listings.Count(x => x.MateriaCount == 5);
        var snapshot = new MarketBoardReceiveSnapshot(
            itemId, listings.Count, withMateria, fiveMateria, DateTimeOffset.Now);
        lock (sync) latest = snapshot;
        services.Log.Information(
            "[MarketBoardDiagnostic] Item {ItemId}: {Listings} listing(s), {WithMateria} with materia, {FiveMateria} verified 5/5.",
            itemId, listings.Count, withMateria, fiveMateria);
    }

    public void Dispose() => services.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
}

internal sealed record MarketBoardReceiveSnapshot(
    uint ItemId,
    int TotalListings,
    int WithMateriaListings,
    int FiveMateriaListings,
    DateTimeOffset ReceivedAt);
