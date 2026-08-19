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

    public static int GradeForSlot(int slot) => slot <= 3 ? 12 : 11;
}
