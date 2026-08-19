using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PentaPenta.Melding;
using PentaPenta.Models;

namespace PentaPenta;

internal sealed class MainWindow : Window
{
    private readonly Services services;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private readonly MeldController controller;
    private readonly MateriaDiagnostics diagnostics;
    private List<InventoryGear> gear = [];
    private readonly HashSet<string> selected = [];
    private string filter = "";
    private bool armSingleMeld;
    private bool armAdvancedMeld;
    private bool armFullRun;

    public MainWindow(Services services, Configuration config, InventoryScanner scanner, MeldController controller, MateriaDiagnostics diagnostics)
        : base("PentaPenta###PentaPentaMain", ImGuiWindowFlags.NoScrollbar)
    {
        this.services = services; this.config = config; this.scanner = scanner; this.controller = controller; this.diagnostics = diagnostics;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 460), MaximumSize = new Vector2(float.MaxValue) };
        Refresh();
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Select inventory gear, arrange your queue, then open Materia Melding. Each item is tracked by bag and slot so duplicate rings remain distinct.");
        ImGui.Separator();
        if (ImGui.Button("Refresh inventory")) Refresh();
        ImGui.SameLine(); ImGui.SetNextItemWidth(260); ImGui.InputTextWithHint("##filter", "Filter gear...", ref filter, 100);
        ImGui.SameLine(); ImGui.TextDisabled($"{selected.Count} selected");

        if (ImGui.BeginTable("gear", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(0, 260)))
        {
            ImGui.TableSetupColumn("Queue", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Item"); ImGui.TableSetupColumn("Location");
            ImGui.TableSetupColumn("Melds", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Overmeld", ImGuiTableColumnFlags.WidthFixed, 75); ImGui.TableHeadersRow();
            foreach (var item in gear.Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                var key = Key(item); var check = selected.Contains(key);
                ImGui.PushID(key); ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Checkbox("##select", ref check)) { if (check) selected.Add(key); else selected.Remove(key); SaveQueue(); }
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name + (item.Hq ? " ★" : ""));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"Bag {BagNumber(item.Container)} / slot {item.Slot + 1}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{item.MeldCount}/5");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.AdvancedMeldingPermitted ? "Yes" : "No"); ImGui.PopID();
            }
            ImGui.EndTable();
        }

        ImGui.Text("Plan: Critical Hit  >  Direct Hit  >  Determination");
        ImGui.TextDisabled("Slots 1–3: grade XII   |   Slots 4–5: grade XI   |   strict no-overcap");
        ImGui.Separator(); ImGui.TextWrapped($"Status: {controller.Status}");
        if (ImGui.Button("Prepare queue")) controller.Load(gear.Where(x => selected.Contains(Key(x))));
        ImGui.SameLine();
        ImGui.Checkbox("Arm full run", ref armFullRun);
        ImGui.SameLine();
        var fullRunWasArmed = armFullRun;
        if (!fullRunWasArmed) ImGui.BeginDisabled();
        if (ImGui.Button("Start full run"))
        {
            controller.Start();
            armFullRun = false;
        }
        if (!fullRunWasArmed) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Stop")) controller.Stop();
        ImGui.Separator();
        ImGui.TextDisabled("Read-only validation (does not click or meld):");
        if (ImGui.Button("Capture Materia window map")) diagnostics.Capture();
        ImGui.SameLine(); ImGui.TextWrapped(diagnostics.LastResult);
        if (ImGui.Button("Validate open choice (no meld)")) controller.ValidateOpenDetail();
        ImGui.Separator();
        ImGui.Checkbox("Arm one guaranteed meld", ref armSingleMeld);
        var wasArmed = armSingleMeld;
        if (!wasArmed) ImGui.BeginDisabled();
        if (ImGui.Button("Meld one verified materia"))
        {
            controller.ExecuteOneVerifiedGuaranteedMeld();
            armSingleMeld = false;
        }
        if (!wasArmed) ImGui.EndDisabled();
        ImGui.Checkbox("Arm one bulk advanced meld", ref armAdvancedMeld);
        var advancedWasArmed = armAdvancedMeld;
        if (!advancedWasArmed) ImGui.BeginDisabled();
        if (ImGui.Button("Run one verified advanced meld"))
        {
            controller.ExecuteOneVerifiedAdvancedMeld();
            armAdvancedMeld = false;
        }
        if (!advancedWasArmed) ImGui.EndDisabled();
    }

    private void Refresh() { gear = scanner.Scan(); selected.Clear(); foreach (var q in config.Queue) selected.Add($"{q.Container}:{q.Slot}:{q.ItemId}:{q.Hq}"); }
    private void SaveQueue()
    {
        config.Queue = gear.Where(x => selected.Contains(Key(x))).Select(x => new QueuedItem(x.ItemId, x.Name, (int)x.Container, x.Slot, x.Hq)).ToList();
        services.PluginInterface.SavePluginConfig(config);
    }
    private static string Key(InventoryGear x) => $"{(int)x.Container}:{x.Slot}:{x.ItemId}:{x.Hq}";
    private static int BagNumber(Dalamud.Game.Inventory.GameInventoryType type) => type switch
    {
        Dalamud.Game.Inventory.GameInventoryType.Inventory1 => 1,
        Dalamud.Game.Inventory.GameInventoryType.Inventory2 => 2,
        Dalamud.Game.Inventory.GameInventoryType.Inventory3 => 3,
        Dalamud.Game.Inventory.GameInventoryType.Inventory4 => 4,
        _ => (int)type
    };
}
