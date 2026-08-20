using Dalamud.Configuration;

namespace PentaPenta;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool StrictNoOvercap { get; set; } = true;
    public float UiCooldownSeconds { get; set; } = 2.5f;
    public List<QueuedItem> Queue { get; set; } = [];
    public Dictionary<uint, CraftingMeldPreset> CraftingPresets { get; set; } = [];
}

public sealed record QueuedItem(uint ItemId, string Name, int Container, uint Slot, bool Hq);

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
    public List<CraftingMateria> Slots { get; set; } =
    [
        CraftingMateria.None, CraftingMateria.None, CraftingMateria.None,
        CraftingMateria.None, CraftingMateria.None,
    ];
}
