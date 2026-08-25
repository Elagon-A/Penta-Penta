namespace PentaPenta.Melding;

internal enum MeldStat { CriticalHit, DirectHit, Determination, Craftsmanship, Cp, Control, Gathering, Perception, Gp }

internal sealed record MateriaChoice(MeldStat Stat, uint Grade11ItemId, uint Grade12ItemId, int Grade11Gain, int Grade12Gain);

internal static class MeldPlan
{
    public static readonly MateriaChoice[] Priority =
    [
        new(MeldStat.CriticalHit, 41759, 41772, 18, 54),
        new(MeldStat.DirectHit, 41758, 41771, 18, 54),
        new(MeldStat.Determination, 41760, 41773, 18, 54),
    ];

    public static readonly MateriaChoice[] CraftingPriority =
    [
        new(MeldStat.Craftsmanship, 41765, 41778, 22, 33),
        new(MeldStat.Cp, 41766, 41779, 9, 11),
        new(MeldStat.Control, 41767, 41780, 15, 23),
    ];

    public static readonly MateriaChoice[] GatheringPriority =
    [
        new(MeldStat.Gathering, 41762, 41775, 20, 36),
        new(MeldStat.Gp, 41764, 41777, 9, 11),
        new(MeldStat.Perception, 41763, 41776, 20, 36),
    ];

    public static MateriaChoice[] PriorityFor(bool crafting, bool gathering) =>
        crafting ? CraftingPriority : gathering ? GatheringPriority : Priority;

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
        CraftingMateria.GatheringXII => new("Gatherer's Guerdon Materia XII", 41775, 36, 12),
        CraftingMateria.GatheringXI => new("Gatherer's Guerdon Materia XI", 41762, 20, 11),
        CraftingMateria.PerceptionXII => new("Gatherer's Guile Materia XII", 41776, 36, 12),
        CraftingMateria.PerceptionXI => new("Gatherer's Guile Materia XI", 41763, 20, 11),
        CraftingMateria.GpXII => new("Gatherer's Grasp Materia XII", 41777, 11, 12),
        CraftingMateria.GpXI => new("Gatherer's Grasp Materia XI", 41764, 9, 11),
        _ => null,
    };
}

internal sealed record PlannedMateria(string Name, uint ItemId, int Gain, int Grade);
