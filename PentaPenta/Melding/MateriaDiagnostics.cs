using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Utility;

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
                var typeName = value.Type.ToString();
                var rendered = typeName switch
                {
                    "Bool" => (value.Byte != 0).ToString(),
                    "Int" => value.Int.ToString(),
                    "UInt" => value.UInt.ToString(),
                    "Float" => value.Float.ToString("0.###"),
                    "String" or "String8" => value.String.ExtractText(),
                    _ => ""
                };

                if ((int)value.Type != 0)
                    lines.Add($"[{i}] {typeName} ({(int)value.Type}): {rendered}");
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
