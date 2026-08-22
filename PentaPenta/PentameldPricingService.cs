using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PentaPenta;

internal sealed class PentameldPricingService : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public PentameldPricingService()
    {
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PentaPenta", "0.1.52"));
    }

    public async Task<IReadOnlyList<PentameldPriceResult>> ScanAsync(
        uint worldId,
        IReadOnlyList<PentameldPricingWatchItem> items,
        IReadOnlySet<string> ownRetainers,
        int undercutGil)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return await ScanItemAsync(worldId, item, ownRetainers, undercutGil).ConfigureAwait(false); }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<PentameldPriceResult> ScanItemAsync(
        uint worldId,
        PentameldPricingWatchItem item,
        IReadOnlySet<string> ownRetainers,
        int undercutGil)
    {
        try
        {
            var url = $"https://universalis.app/api/v2/{worldId}/{item.ItemId}?listings=100&entries=0";
            await using var stream = await http.GetStreamAsync(url).ConfigureAwait(false);
            var response = await JsonSerializer.DeserializeAsync<UniversalisResponse>(stream).ConfigureAwait(false);
            var matches = (response?.Listings ?? [])
                .Where(x => x.Hq == item.Hq
                    && x.UnitPrice > 0
                    && x.Materia.Count == 5
                    && !ownRetainers.Contains(x.RetainerName ?? ""))
                .ToList();
            var cheapest = matches.Count == 0 ? (int?)null : matches.Min(x => x.UnitPrice);
            int? proposed = cheapest is null ? null : Math.Max(1, cheapest.Value - Math.Max(0, undercutGil));
            return new PentameldPriceResult(item.ItemId, item.Name, item.Hq, matches.Count, cheapest, proposed, null);
        }
        catch (Exception ex)
        {
            return new PentameldPriceResult(item.ItemId, item.Name, item.Hq, 0, null, null, ex.Message);
        }
    }

    public void Dispose() => http.Dispose();

    private sealed class UniversalisResponse
    {
        [JsonPropertyName("listings")]
        public List<UniversalisListing> Listings { get; set; } = [];
    }

    private sealed class UniversalisListing
    {
        [JsonPropertyName("unitPrice")]
        public int UnitPrice { get; set; }

        [JsonPropertyName("hq")]
        public bool Hq { get; set; }

        [JsonPropertyName("retainerName")]
        public string? RetainerName { get; set; }

        [JsonPropertyName("materia")]
        public List<UniversalisMateria> Materia { get; set; } = [];
    }

    private sealed class UniversalisMateria;
}

internal sealed record PentameldPriceResult(
    uint ItemId,
    string Name,
    bool Hq,
    int QualifyingListings,
    int? CheapestPrice,
    int? ProposedPrice,
    string? Error);
