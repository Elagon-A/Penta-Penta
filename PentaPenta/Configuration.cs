using Dalamud.Configuration;

namespace PentaPenta;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool StrictNoOvercap { get; set; } = true;
    public float UiCooldownSeconds { get; set; } = 2.5f;
    public bool EnableMarketBoardOverlay { get; set; } = true;
    public bool AutoCaptureMarketBoardListings { get; set; } = true;
    public List<QueuedItem> Queue { get; set; } = [];
    public Dictionary<uint, CraftingMeldPreset> CraftingPresets { get; set; } = [];
    public List<CraftingMeldTemplate> CraftingMeldTemplates { get; set; } = [];
    public Dictionary<uint, long> MateriaConsumedHistory { get; set; } = [];
    public List<PentameldPricingWatchItem> PentameldPricingWatchList { get; set; } = [];
    public int PentameldPricingUndercutGil { get; set; } = 1;
    public string PentameldPricingOwnRetainers { get; set; } = "";
    public int MaxSingleRepriceDecreasePercent { get; set; } = 10;
}

public sealed record QueuedItem(uint ItemId, string Name, int Container, uint Slot, bool Hq);

public sealed class PentameldPricingWatchItem
{
    public uint ItemId { get; set; }
    public string Name { get; set; } = "";
    public bool Hq { get; set; }
}

public enum CraftingMateria
{
    None,
    CraftsmanshipXII,
    CraftsmanshipXI,
    ControlXII,
    ControlXI,
    CpXII,
    CpXI,
}

public sealed class CraftingMeldPreset
{
    // Defaults to true so presets saved by versions before 0.1.25 remain active.
    public bool Enabled { get; set; } = true;
    public List<CraftingMateria> Slots { get; set; } =
    [
        CraftingMateria.None, CraftingMateria.None, CraftingMateria.None,
        CraftingMateria.None, CraftingMateria.None,
    ];
}

public sealed class CraftingMeldTemplate
{
    public string Name { get; set; } = "New template";
    public List<CraftingMateria> Slots { get; set; } =
    [
        CraftingMateria.None, CraftingMateria.None, CraftingMateria.None,
        CraftingMateria.None, CraftingMateria.None,
    ];
}
