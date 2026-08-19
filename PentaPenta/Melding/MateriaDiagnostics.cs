using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PentaPenta.Melding;

internal sealed class MateriaDiagnostics(Services services)
{
    public string LastResult { get; private set; } = "No capture yet.";

    public unsafe void Capture()
    {
        try
        {
            var addon = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttach");
            if (addon == null || !addon->IsReady)
            {
                LastResult = "Open Materia Melding and select an item first.";
                return;
            }

            var lines = new List<string>();
            var values = addon->AtkValues;
            var count = addon->AtkValuesCount;
            for (var i = 0; i < count; i++)
            {
                ref var value = ref values[i];
                var rendered = (int)value.Type switch
                {
                    2 => (value.Byte != 0).ToString(),
                    3 => value.Int.ToString(),
                    4 => value.UInt.ToString(),
                    5 => value.Float.ToString("0.###"),
                    6 or 7 => value.String.ToString(),
                    _ => ""
                };

                if (!string.IsNullOrWhiteSpace(rendered))
                    lines.Add($"[{i}] {value.Type}: {rendered}");
            }

            LastResult = $"Captured {lines.Count} populated values. See dalamud.log.";
            services.Log.Information("PentaPenta MateriaAttach diagnostic ({Count} values):\n{Values}", count, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            LastResult = "Capture failed; see dalamud.log.";
            services.Log.Error(ex, "PentaPenta MateriaAttach diagnostic failed");
        }
    }
}
