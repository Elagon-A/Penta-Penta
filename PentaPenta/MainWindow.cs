using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Windowing;
using PentaPenta.Melding;
using PentaPenta.Models;

namespace PentaPenta;

internal sealed class MainWindow : Window
{
    private static readonly string[] CraftingMateriaLabels =
    [
        "Not set", "Craftsmanship XII", "Craftsmanship XI", "Control XII",
        "Control XI", "CP XII", "CP XI",
    ];
    private readonly Services services;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private readonly MeldController controller;
    private List<InventoryGear> gear = [];
    private readonly HashSet<string> selected = [];
    private List<MateriaStock> materiaStock = [];
    private DateTime nextMateriaStockRefresh;
    private string filter = "";
    private bool armFullRun;
    private CraftingMeldPreset? copiedCraftingPreset;
    private string copiedCraftingPresetSource = "";
    private string presetCopyStatus = "";

    public MainWindow(Services services, Configuration config, InventoryScanner scanner, MeldController controller)
        : base("PentaPenta###PentaPentaMain")
    {
        this.services = services; this.config = config; this.scanner = scanner; this.controller = controller;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 500), MaximumSize = new Vector2(float.MaxValue) };
        Refresh();
    }

    public override void Draw()
    {
        RefreshMateriaStockIfDue();
        if (!ImGui.BeginTabBar("main-tabs")) return;
        if (ImGui.BeginTabItem("Queue"))
        {
            DrawQueueTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Materia History"))
        {
            DrawMateriaHistory();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawQueueTab()
    {
        ImGui.TextWrapped("Select inventory gear, arrange your queue, then open Materia Melding. Each item is tracked by bag and slot so duplicate rings remain distinct.");
        ImGui.Separator();
        if (ImGui.Button("Refresh inventory")) Refresh();
        ImGui.SameLine();
        if (ImGui.Button("Select all"))
        {
            selected.Clear();
            foreach (var item in gear) selected.Add(Key(item));
            SaveQueue();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear all"))
        {
            selected.Clear();
            SaveQueue();
        }
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

        DrawExactItemPresetEditor();

        var enableMarketOverlay = config.EnableMarketBoardOverlay;
        if (ImGui.Checkbox("Enable marketboard materia overlay", ref enableMarketOverlay))
        {
            config.EnableMarketBoardOverlay = enableMarketOverlay;
            services.PluginInterface.SavePluginConfig(config);
        }
        ImGui.TextDisabled("Shows the clickable materia shopping list when you approach a marketboard.");

        ImGui.TextUnformatted("Materia inventory");
        ImGui.SameLine();
        ImGui.TextDisabled("live · low-stock warning below 25");
        if (ImGui.BeginTable("materia-stock", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Stat");
            ImGui.TableSetupColumn("Grade XII", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Grade XI", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var stock in materiaStock)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(stock.Stat);
                ImGui.TableNextColumn(); DrawStockCount(stock.Grade12);
                ImGui.TableNextColumn(); DrawStockCount(stock.Grade11);
            }
            ImGui.EndTable();
        }

        ImGui.Text("Plan: Critical Hit  >  Direct Hit  >  Determination");
        ImGui.Text("DoH default: Craftsmanship  >  CP  >  Control");
        ImGui.TextDisabled("Grade XII: native slots + first overmeld   |   later slots: grade XI   |   strict no-overcap");
        ImGui.Separator();
        ImGui.TextWrapped($"Status: {controller.Status}");
        if (controller.IsQueueRunning || controller.TotalMelds > 0)
        {
            var fraction = controller.TotalMelds == 0 ? 0f : controller.CompletedMelds / (float)controller.TotalMelds;
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{controller.CompletedMelds} / {controller.TotalMelds} melds");
            if (controller.QueueCount > 0 && controller.CurrentItemName.Length > 0)
                ImGui.TextUnformatted($"Item {controller.QueuePosition}/{controller.QueueCount}: {controller.CurrentItemName}");
            var eta = controller.EstimatedRemaining is { } remaining ? FormatDuration(remaining) : "calculating...";
            ImGui.TextDisabled($"Elapsed {FormatDuration(controller.Elapsed)}   |   ETA {eta}   |   Materia consumed {controller.MateriaConsumed}");
        }
        if (ImGui.Button("Prepare queue")) controller.PrepareAndOpenMelding(gear.Where(x => selected.Contains(Key(x))));
        ImGui.SameLine();
        ImGui.Checkbox("Arm full run", ref armFullRun);
        ImGui.SameLine();
        var fullRunWasArmed = armFullRun;
        if (!fullRunWasArmed) ImGui.BeginDisabled();
        if (ImGui.Button("Start queue"))
        {
            controller.Start();
            armFullRun = false;
        }
        if (!fullRunWasArmed) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Stop")) controller.Stop();
        ImGui.TextDisabled("Keep Materia Melding open and do not interact with either window while the queue is running.");
    }

    private void DrawMateriaHistory()
    {
        var history = config.MateriaConsumedHistory
            .Where(x => x.Value > 0)
            .Select(x => new
            {
                ItemId = x.Key,
                Name = services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(x.Key)?.Name.ExtractText() ?? $"Item {x.Key}",
                Count = x.Value,
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name)
            .ToList();
        var total = history.Sum(x => x.Count);
        ImGui.TextUnformatted($"Total materia consumed: {total:N0}");
        ImGui.TextDisabled("History begins with version 0.1.48 and persists across plugin updates.");
        ImGui.Separator();
        if (!ImGui.BeginTable("materia-history", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1))) return;
        ImGui.TableSetupColumn("Materia");
        ImGui.TableSetupColumn("Consumed", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableHeadersRow();
        foreach (var row in history)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Count.ToString("N0"));
        }
        ImGui.EndTable();
    }

    private void Refresh()
    {
        gear = scanner.Scan();
        var available = gear.Select(Key).ToHashSet();
        selected.Clear();
        foreach (var q in config.Queue)
        {
            var key = $"{q.Container}:{q.Slot}:{q.ItemId}:{q.Hq}";
            if (available.Contains(key)) selected.Add(key);
        }

        if (selected.Count != config.Queue.Count) SaveQueue();
        materiaStock = scanner.ScanMateriaStock();
        nextMateriaStockRefresh = DateTime.UtcNow.AddMilliseconds(250);
    }
    internal void SelectInventoryItem(GameInventoryItem target)
    {
        Refresh();
        var match = gear.FirstOrDefault(x => x.Container == target.ContainerType
            && x.Slot == target.InventorySlot
            && x.ItemId == target.BaseItemId
            && x.Hq == target.IsHq);
        if (match is null) return;

        selected.Clear();
        selected.Add(Key(match));
        SaveQueue();
        controller.Load([match]);
        IsOpen = true;
    }
    private void RefreshMateriaStockIfDue()
    {
        if (DateTime.UtcNow < nextMateriaStockRefresh) return;
        materiaStock = scanner.ScanMateriaStock();
        nextMateriaStockRefresh = DateTime.UtcNow.AddMilliseconds(250);
    }
    private void DrawExactItemPresetEditor()
    {
        var selectedItems = gear.Where(x => selected.Contains(Key(x))).ToList();
        if (copiedCraftingPreset is not null && selectedItems.Count > 0)
        {
            var targetTypes = selectedItems.Select(x => x.ItemId).Distinct().ToList();
            ImGui.TextDisabled($"Copied preset: {copiedCraftingPresetSource}");
            if (ImGui.Button($"Apply copied preset to {targetTypes.Count} checked item type(s)"))
            {
                foreach (var itemId in targetTypes)
                    config.CraftingPresets[itemId] = ClonePreset(copiedCraftingPreset, enabled: true);
                services.PluginInterface.SavePluginConfig(config);
                presetCopyStatus = $"Applied {copiedCraftingPresetSource} preset to {targetTypes.Count} checked item type(s).";
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear copied preset"))
            {
                copiedCraftingPreset = null;
                copiedCraftingPresetSource = "";
            }
            if (presetCopyStatus.Length > 0) ImGui.TextDisabled(presetCopyStatus);
            ImGui.Separator();
        }
        if (selectedItems.Count != 1) return;
        var item = selectedItems[0];
        config.CraftingPresets.TryGetValue(item.ItemId, out var preset);
        var enabled = preset?.Enabled ?? false;
        if (ImGui.Checkbox($"Use exact crafting preset for {item.Name}", ref enabled))
        {
            if (preset is null)
            {
                preset = new CraftingMeldPreset { Enabled = enabled };
                config.CraftingPresets[item.ItemId] = preset;
            }
            else
            {
                preset.Enabled = enabled;
            }
            services.PluginInterface.SavePluginConfig(config);
        }
        if (!enabled || preset is null) return;

        while (preset.Slots.Count < 5) preset.Slots.Add(CraftingMateria.None);
        for (var i = 0; i < 5; i++)
        {
            ImGui.PushID($"craft-preset-{i}");
            ImGui.SetNextItemWidth(210);
            var value = (int)preset.Slots[i];
            if (ImGui.Combo($"Slot {i + 1}", ref value, CraftingMateriaLabels, CraftingMateriaLabels.Length))
            {
                preset.Slots[i] = (CraftingMateria)value;
                services.PluginInterface.SavePluginConfig(config);
            }
            ImGui.PopID();
        }
        var presetComplete = preset.Slots.Take(5).All(x => x != CraftingMateria.None);
        if (!presetComplete) ImGui.BeginDisabled();
        if (ImGui.Button("Copy this preset"))
        {
            copiedCraftingPreset = ClonePreset(preset, enabled: true);
            copiedCraftingPresetSource = item.Name;
            presetCopyStatus = $"Copied {item.Name}. Check the target items, then apply it.";
        }
        if (!presetComplete) ImGui.EndDisabled();
        if (!presetComplete)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Set all five slots before copying.");
        }
        ImGui.TextDisabled("This exact slot plan replaces combat-stat priority for this item type.");
    }
    private static CraftingMeldPreset ClonePreset(CraftingMeldPreset source, bool enabled) => new()
    {
        Enabled = enabled,
        Slots = source.Slots.Take(5).Concat(Enumerable.Repeat(CraftingMateria.None, 5)).Take(5).ToList(),
    };
    private static void DrawStockCount(int count)
    {
        var color = count == 0
            ? new Vector4(1f, 0.3f, 0.3f, 1f)
            : count < 25
                ? new Vector4(1f, 0.75f, 0.25f, 1f)
                : new Vector4(0.65f, 1f, 0.65f, 1f);
        ImGui.TextColored(color, count.ToString("N0"));
    }
    private void SaveQueue()
    {
        config.Queue = gear.Where(x => selected.Contains(Key(x))).Select(x => new QueuedItem(x.ItemId, x.Name, (int)x.Container, x.Slot, x.Hq)).ToList();
        services.PluginInterface.SavePluginConfig(config);
    }
    private static string Key(InventoryGear x) => $"{(int)x.Container}:{x.Slot}:{x.ItemId}:{x.Hq}";
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    private static int BagNumber(Dalamud.Game.Inventory.GameInventoryType type) => type switch
    {
        Dalamud.Game.Inventory.GameInventoryType.Inventory1 => 1,
        Dalamud.Game.Inventory.GameInventoryType.Inventory2 => 2,
        Dalamud.Game.Inventory.GameInventoryType.Inventory3 => 3,
        Dalamud.Game.Inventory.GameInventoryType.Inventory4 => 4,
        _ => (int)type
    };
}
