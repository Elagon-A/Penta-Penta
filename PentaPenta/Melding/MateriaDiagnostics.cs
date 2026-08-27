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
            var captured = new List<string>();
            if (CaptureAddon("MateriaAttach")) captured.Add("MateriaAttach");
            if (CaptureAddon("MateriaAttachDialog")) captured.Add("MateriaAttachDialog");

            if (captured.Count == 0)
            {
                LastResult = "No ready Materia window found.";
                return;
            }

            LastResult = $"Captured {string.Join(" + ", captured)}. See dalamud.log.";
        }
        catch (Exception ex)
        {
            LastResult = "Capture failed; see dalamud.log.";
            services.Log.Error(ex, "PentaPenta Materia diagnostic failed");
        }
    }

    public unsafe void CaptureRetrieval()
    {
        try
        {
            var captured = new List<string>();
            if (CaptureAddon("MateriaRetrieveDialog")) captured.Add("MateriaRetrieveDialog");
            if (CaptureAddon("SelectYesno")) captured.Add("SelectYesno");

            if (captured.Count == 0)
            {
                LastResult = "No ready Materia Retrieval window found.";
                return;
            }

            LastResult = $"Captured {string.Join(" + ", captured)}. See dalamud.log.";
        }
        catch (Exception ex)
        {
            LastResult = "Retrieval capture failed; see dalamud.log.";
            services.Log.Error(ex, "PentaPenta Materia Retrieval diagnostic failed");
        }
    }

    private unsafe bool CaptureAddon(string addonName)
    {
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>(addonName);
        if (addon == null || !addon->IsReady) return false;

        try
        {
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

            services.Log.Information("PentaPenta {Addon} diagnostic ({Count} values):\n{Values}", addonName, count, string.Join("\n", lines));
            return true;
        }
        catch (Exception ex)
        {
            services.Log.Error(ex, "PentaPenta {Addon} diagnostic failed", addonName);
            return false;
        }
    }
}
