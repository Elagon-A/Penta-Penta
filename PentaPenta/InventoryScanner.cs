using Dalamud.Game.Inventory;
using Lumina.Excel.Sheets;
using PentaPenta.Models;

namespace PentaPenta;

internal sealed class InventoryScanner(Services services)
{
    private static readonly GameInventoryType[] Bags =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2,
        GameInventoryType.Inventory3, GameInventoryType.Inventory4
    ];

    public List<InventoryGear> Scan()
    {
        var result = new List<InventoryGear>();
        if (!services.ClientState.IsLoggedIn)
            return result;

        foreach (var bag in Bags)
        foreach (ref readonly var slot in services.Inventory.GetInventoryItems(bag))
        {
            if (slot.IsEmpty) continue;
            var item = services.Data.GetExcelSheet<Item>().GetRowOrDefault(slot.BaseItemId);
            if (item is null || item.Value.EquipSlotCategory.RowId == 0 || item.Value.MateriaSlotCount == 0) continue;
            result.Add(new InventoryGear(slot.BaseItemId, item.Value.Name.ExtractText(), bag,
                slot.InventorySlot, slot.IsHq, slot.MateriaEntries.Count(x => x.Type.RowId != 0),
                item.Value.MateriaSlotCount, item.Value.IsAdvancedMeldingPermitted));
        }
        return result.OrderBy(x => x.Name).ThenBy(x => x.Container).ThenBy(x => x.Slot).ToList();
    }
}
