using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PentaPenta.Models;

namespace PentaPenta.Melding;

internal enum RunState { Idle, WaitingForMeldingWindow, Ready, Running, Paused, Complete, Error }

internal sealed class MeldController(Services services, Configuration config)
{
    public RunState State { get; private set; } = RunState.Idle;
    public string Status { get; private set; } = "Idle";
    public IReadOnlyList<InventoryGear> Items => items;
    private readonly List<InventoryGear> items = [];

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

    private void Fail(string message) { State = RunState.Error; Status = message; services.Log.Warning(message); }
}
