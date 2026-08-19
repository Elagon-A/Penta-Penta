using Dalamud.Game.Inventory;

namespace PentaPenta.Models;

internal sealed record InventoryGear(
    uint ItemId,
    string Name,
    GameInventoryType Container,
    uint Slot,
    bool Hq,
    int MeldCount,
    int MateriaSlotCount,
    bool AdvancedMeldingPermitted);
