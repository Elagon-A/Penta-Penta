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

    internal static bool IsPlayerBag(GameInventoryType type) => Bags.Contains(type);

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
            var jobs = services.Data.GetExcelSheet<ClassJobCategory>().GetRowOrDefault(item.Value.ClassJobCategory.RowId);
            var isCraftingGear = jobs is not null && (jobs.Value.CRP || jobs.Value.BSM || jobs.Value.ARM
                || jobs.Value.GSM || jobs.Value.LTW || jobs.Value.WVR || jobs.Value.ALC || jobs.Value.CUL);
            var isGatheringGear = jobs is not null && (jobs.Value.MIN || jobs.Value.BTN || jobs.Value.FSH);
            result.Add(new InventoryGear(slot.BaseItemId, item.Value.Name.ExtractText(), bag,
                slot.InventorySlot, slot.IsHq, slot.MateriaEntries.Count(x => x.Type.RowId != 0),
                item.Value.MateriaSlotCount, item.Value.IsAdvancedMeldingPermitted, isCraftingGear, isGatheringGear));
        }
        return result.OrderBy(x => x.Name).ThenBy(x => x.Container).ThenBy(x => x.Slot).ToList();
    }

    public List<MateriaStock> ScanMateriaStock()
    {
        var counts = new Dictionary<uint, int>();
        foreach (var bag in Bags)
        foreach (ref readonly var slot in services.Inventory.GetInventoryItems(bag))
        {
            if (slot.IsEmpty) continue;
            counts[slot.BaseItemId] = counts.GetValueOrDefault(slot.BaseItemId) + slot.Quantity;
        }

        return
        [
            new("Critical Hit", counts.GetValueOrDefault(41772u), counts.GetValueOrDefault(41759u)),
            new("Direct Hit", counts.GetValueOrDefault(41771u), counts.GetValueOrDefault(41758u)),
            new("Determination", counts.GetValueOrDefault(41773u), counts.GetValueOrDefault(41760u)),
            new("Craftsmanship", counts.GetValueOrDefault(41778u), counts.GetValueOrDefault(41765u)),
            new("Control", counts.GetValueOrDefault(41780u), counts.GetValueOrDefault(41767u)),
            new("CP", counts.GetValueOrDefault(41779u), counts.GetValueOrDefault(41766u)),
            new("Gathering", counts.GetValueOrDefault(41781u), counts.GetValueOrDefault(41768u)),
            new("Perception", counts.GetValueOrDefault(41782u), counts.GetValueOrDefault(41769u)),
            new("GP", counts.GetValueOrDefault(41783u), counts.GetValueOrDefault(41770u)),
        ];
    }

    public Dictionary<uint, int> ScanItemCounts(IEnumerable<uint> itemIds)
    {
        var wanted = itemIds.ToHashSet();
        var counts = wanted.ToDictionary(x => x, _ => 0);
        foreach (var bag in Bags)
        foreach (ref readonly var slot in services.Inventory.GetInventoryItems(bag))
            if (!slot.IsEmpty && wanted.Contains(slot.BaseItemId))
                counts[slot.BaseItemId] += slot.Quantity;
        return counts;
    }
}

internal sealed record MateriaStock(string Stat, int Grade12, int Grade11);
