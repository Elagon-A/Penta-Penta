using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PentaPenta.Models;

namespace PentaPenta.Melding;

internal enum RunState { Idle, WaitingForMeldingWindow, Ready, Running, Paused, Complete, Error }

internal sealed class MeldController : IDisposable
{
    private readonly Services services;
    private readonly Configuration config;
    public RunState State { get; private set; } = RunState.Idle;
    public string Status { get; private set; } = "Idle";
    public IReadOnlyList<InventoryGear> Items => items;
    private readonly List<InventoryGear> items = [];
    private AdvancedPhase advancedPhase;
    private DateTime advancedDeadline;
    private uint pendingMateriaId;
    private int startingMateriaCount;
    private int startingMeldCount;

    public MeldController(Services services, Configuration config)
    {
        this.services = services;
        this.config = config;
        services.Framework.Update += OnFrameworkUpdate;
    }

    public void Load(IEnumerable<InventoryGear> selected)
    {
        items.Clear();
        items.AddRange(selected);
        State = items.Count == 0 ? RunState.Idle : RunState.WaitingForMeldingWindow;
        Status = items.Count == 0 ? "Select at least one item." : "Open Materia Melding, then press Start.";
    }

    public void Start()
    {
        if (items.Count == 0) { Fail("The queue is empty."); return; }
        if (!services.ClientState.IsLoggedIn) { Fail("You are not logged in."); return; }
        if (services.Condition[ConditionFlag.InCombat]) { Fail("Cannot start in combat."); return; }
        if (services.GameGui.GetAddonByName("MateriaAttach").IsNull)
        { State = RunState.WaitingForMeldingWindow; Status = "Open the Materia Melding window."; return; }

        // Deliberately gated until the live client row/callback map is validated for this patch.
        // The queue, identity checks, inventory snapshots and plan are ready; sending a stale
        // callback here could consume expensive materia on the wrong item.
        State = RunState.Ready;
        Status = "Queue verified. Automation driver needs current-patch callback validation.";
    }

    public void Stop() { State = RunState.Idle; Status = "Stopped by user."; }

    public unsafe void ValidateOpenDetail()
    {
        if (items.Count == 0) { Fail("Prepare the queue first."); return; }
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttachDialog");
        if (addon == null || !addon->IsReady || addon->AtkValuesCount <= 16)
        { Fail("Open a materia detail window first."); return; }

        var materiaName = addon->AtkValues[9].String.ExtractText().Trim();
        var gainText = addon->AtkValues[10].String.ExtractText().Trim();
        var equipmentName = addon->AtkValues[16].String.ExtractText().Trim();
        var expectedItem = items[0];
        if (!equipmentName.StartsWith(expectedItem.Name, StringComparison.Ordinal))
        { Fail($"Selected item mismatch: expected {expectedItem.Name}, found {equipmentName}."); return; }

        var slot = expectedItem.MeldCount + 1;
        var expectedGrade = MeldPlan.GradeForSlot(slot);
        var choice = MeldPlan.Priority.FirstOrDefault(x => materiaName.Contains(x.Stat switch
        {
            MeldStat.CriticalHit => "Savage Aim",
            MeldStat.DirectHit => "Heavens' Eye",
            _ => "Savage Might"
        }, StringComparison.Ordinal));
        if (choice is null) { Fail($"Unsupported materia: {materiaName}."); return; }

        var expectedGain = expectedGrade == 12 ? choice.Grade12Gain : choice.Grade11Gain;
        if (!materiaName.EndsWith(expectedGrade == 12 ? "XII" : "XI", StringComparison.Ordinal))
        { Fail($"Slot {slot} requires grade {expectedGrade}."); return; }
        if (!gainText.Contains($"+{expectedGain}", StringComparison.Ordinal))
        { Fail($"Strict no-overcap rejected {materiaName}: displayed {gainText}, expected +{expectedGain}."); return; }

        State = RunState.Ready;
        Status = $"SAFE PREVIEW: slot {slot}, {materiaName}, {gainText}, item verified. No callback sent.";
    }

    public unsafe void ExecuteOneVerifiedGuaranteedMeld()
    {
        ValidateOpenDetail();
        if (State != RunState.Ready || items.Count == 0) return;

        var slot = items[0].MeldCount + 1;
        if (slot > 2)
        { Fail("One-shot test is restricted to guaranteed slots 1–2."); return; }

        var addon = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttachDialog");
        if (addon == null || !addon->IsReady)
        { Fail("Materia detail closed before execution."); return; }

        var args = stackalloc AtkValue[3];
        for (var i = 0; i < 3; i++)
        {
            args[i].Type = AtkValueType.Int;
            args[i].Int = 0;
        }

        State = RunState.Running;
        Status = $"One verified slot-{slot} meld callback sent. Inspect the item before continuing.";
        services.Log.Information("Sending one verified guaranteed meld callback for {Item}, slot {Slot}", items[0].Name, slot);
        addon->FireCallback(3, args, true);
    }

    public unsafe void ExecuteOneVerifiedAdvancedMeld()
    {
        ValidateOpenDetail();
        if (State != RunState.Ready || items.Count == 0) return;

        var slot = items[0].MeldCount + 1;
        if (slot < 3 || slot > 5)
        { Fail("Advanced test is restricted to overmeld slots 3–5."); return; }

        var addon = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttachDialog");
        if (addon == null || !addon->IsReady)
        { Fail("Materia detail closed before execution."); return; }

        var materiaName = addon->AtkValues[9].String.ExtractText().Trim();
        pendingMateriaId = MateriaItemId(materiaName);
        if (pendingMateriaId == 0) { Fail($"Unsupported materia: {materiaName}."); return; }

        startingMateriaCount = CountItem(pendingMateriaId);
        startingMeldCount = items[0].MeldCount;
        if (startingMateriaCount <= 0) { Fail("No matching materia remains."); return; }

        var args = stackalloc AtkValue[3];
        args[0].Type = AtkValueType.Int; args[0].Int = 0;
        args[1].Type = AtkValueType.Int; args[1].Int = 0;
        args[2].Type = AtkValueType.Int; args[2].Int = 1;
        advancedPhase = AdvancedPhase.WaitingForConfirmation;
        advancedDeadline = DateTime.UtcNow.AddSeconds(10);
        State = RunState.Running;
        Status = $"Opening guarded bulk advanced meld for slot {slot}...";
        addon->FireCallback(3, args, true);
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (advancedPhase == AdvancedPhase.None) return;
        if (DateTime.UtcNow > advancedDeadline)
        { advancedPhase = AdvancedPhase.None; Fail("Advanced meld timed out; inspect the item."); return; }

        if (advancedPhase == AdvancedPhase.WaitingForConfirmation)
        {
            var yesno = services.GameGui.GetAddonByName<AtkUnitBase>("SelectYesno");
            if (yesno == null || !yesno->IsReady) return;

            var args = stackalloc AtkValue[1];
            args[0].Type = AtkValueType.Int; args[0].Int = 0;
            services.Log.Information("Confirming one guarded bulk advanced meld");
            yesno->FireCallback(1, args, true);
            advancedPhase = AdvancedPhase.Monitoring;
            advancedDeadline = DateTime.UtcNow.AddMinutes(6);
            Status = "Bulk advanced meld running; monitoring quantity and item state...";
            return;
        }

        var currentCount = CountItem(pendingMateriaId);
        var currentMelds = CurrentMeldCount(items[0]);
        if (currentMelds > startingMeldCount)
        {
            var used = Math.Max(0, startingMateriaCount - currentCount);
            advancedPhase = AdvancedPhase.None;
            State = RunState.Complete;
            Status = $"Advanced meld succeeded; {used} materia consumed. Refresh before continuing.";
        }
        else if (currentCount <= 0)
        {
            advancedPhase = AdvancedPhase.None;
            Fail("Materia reached zero before success; inspect the item.");
        }
    }

    private int CountItem(uint itemId)
    {
        var total = 0;
        foreach (var type in new[] { Dalamud.Game.Inventory.GameInventoryType.Inventory1, Dalamud.Game.Inventory.GameInventoryType.Inventory2, Dalamud.Game.Inventory.GameInventoryType.Inventory3, Dalamud.Game.Inventory.GameInventoryType.Inventory4 })
            foreach (ref readonly var item in services.Inventory.GetInventoryItems(type))
                if (!item.IsEmpty && item.BaseItemId == itemId) total += item.Quantity;
        return total;
    }

    private int CurrentMeldCount(InventoryGear expected)
    {
        var slots = services.Inventory.GetInventoryItems(expected.Container);
        if (expected.Slot >= slots.Length) return -1;
        var item = slots[(int)expected.Slot];
        if (item.IsEmpty || item.BaseItemId != expected.ItemId || item.IsHq != expected.Hq) return -1;
        return item.MateriaEntries.Count(x => x.Type.RowId != 0);
    }

    private static uint MateriaItemId(string name) => name switch
    {
        "Savage Aim Materia XI" => 41759, "Savage Aim Materia XII" => 41772,
        "Savage Might Materia XI" => 41760, "Savage Might Materia XII" => 41773,
        "Heavens' Eye Materia XI" => 41758, "Heavens' Eye Materia XII" => 41771,
        _ => 0
    };

    public void Dispose() => services.Framework.Update -= OnFrameworkUpdate;

    private void Fail(string message) { State = RunState.Error; Status = message; services.Log.Warning(message); }

    private enum AdvancedPhase { None, WaitingForConfirmation, Monitoring }
}
