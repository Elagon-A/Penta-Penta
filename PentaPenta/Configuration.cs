using Dalamud.Configuration;

namespace PentaPenta;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
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

public sealed record QueuedItem(uint ItemId, string Name, int Container, uint Slot, bool Hq)
{
    // A hint used only when an inventory sort moves otherwise-identical items.
    // Location remains the authoritative identity once a queue is prepared.
    public int MeldCount { get; init; } = -1;
}

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
    GatheringXII,
    GatheringXI,
    PerceptionXII,
    PerceptionXI,
    GpXII,
    GpXI,
}

public enum MeldTemplateDiscipline { Crafting, Gathering }

public sealed class CraftingMeldPreset
{
    // Defaults to true so presets saved by versions before 0.1.25 remain active.
    public bool Enabled { get; set; } = true;
    // Keep collection initializers empty. Newtonsoft populates existing collection
    // instances during load; seeding five values here used to prepend five empty
    // slots on every plugin reload.
    public List<CraftingMateria> Slots { get; set; } = [];
}

public sealed class CraftingMeldTemplate
{
    public string Name { get; set; } = "New template";
    public MeldTemplateDiscipline Discipline { get; set; } = MeldTemplateDiscipline.Crafting;
    public List<CraftingMateria> Slots { get; set; } = [];
}
