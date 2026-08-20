namespace PentaPenta.Melding;

internal enum MeldStat { CriticalHit, DirectHit, Determination }

internal sealed record MateriaChoice(MeldStat Stat, uint Grade11ItemId, uint Grade12ItemId, int Grade11Gain, int Grade12Gain);

internal static class MeldPlan
{
    public static readonly MateriaChoice[] Priority =
    [
        new(MeldStat.CriticalHit, 41759, 41772, 18, 54),
        new(MeldStat.DirectHit, 41758, 41771, 18, 54),
        new(MeldStat.Determination, 41760, 41773, 18, 54),
    ];

    // Current-grade materia can be used in every native slot plus the first
    // advanced slot. Later advanced slots require the previous grade.
    public static int GradeForSlot(int slot, int nativeMateriaSlots) =>
        slot <= nativeMateriaSlots + 1 ? 12 : 11;

    public static PlannedMateria? Resolve(CraftingMateria materia) => materia switch
    {
        CraftingMateria.CraftsmanshipXII => new("Craftsman's Competence Materia XII", 41778, 33, 12),
        CraftingMateria.CraftsmanshipXI => new("Craftsman's Competence Materia XI", 41765, 22, 11),
        CraftingMateria.ControlXII => new("Craftsman's Command Materia XII", 41780, 23, 12),
        CraftingMateria.ControlXI => new("Craftsman's Command Materia XI", 41767, 15, 11),
        CraftingMateria.CpXII => new("Craftsman's Cunning Materia XII", 41779, 11, 12),
        CraftingMateria.CpXI => new("Craftsman's Cunning Materia XI", 41766, 9, 11),
        _ => null,
    };
}

internal sealed record PlannedMateria(string Name, uint ItemId, int Gain, int Grade);
