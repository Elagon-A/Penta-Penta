using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
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
    public bool IsQueueRunning => autoPhase != AutoPhase.None;
    public int QueuePosition => items.Count == 0 ? 0 : Math.Clamp(autoItemIndex + 1, 1, items.Count);
    public int QueueCount => items.Count;
    public int CompletedMelds => autoCompletedMelds;
    public int TotalMelds => autoTotalMelds;
    public string CurrentItemName => autoItemIndex >= 0 && autoItemIndex < items.Count ? items[autoItemIndex].Name : "";
    public TimeSpan Elapsed => autoStartedAt == default
        ? TimeSpan.Zero
        : (autoFinishedAt == default ? DateTime.UtcNow : autoFinishedAt) - autoStartedAt;
    public TimeSpan? EstimatedRemaining => autoCompletedMelds <= 0
        ? null
        : TimeSpan.FromTicks((long)(Elapsed.Ticks / (double)autoCompletedMelds * Math.Max(0, autoTotalMelds - autoCompletedMelds)));
    public int MateriaConsumed
    {
        get
        {
            var inProgress = autoPhase == AutoPhase.Monitoring
                ? Math.Max(0, startingMateriaCount - CountItem(pendingMateriaId))
                : 0;
            return autoMateriaConsumed + inProgress;
        }
    }
    private readonly List<InventoryGear> items = [];
    private AdvancedPhase advancedPhase;
    private DateTime advancedDeadline;
    private uint pendingMateriaId;
    private int startingMateriaCount;
    private int startingMeldCount;
    private AutoPhase autoPhase;
    private DateTime autoNextAction;
    private DateTime autoDeadline;
    private int autoCandidateIndex;
    private int autoItemIndex;
    private string autoMateriaName = "";
    private int autoRetries;
    private int autoTotalMelds;
    private int autoCompletedMelds;
    private int autoMateriaConsumed;
    private DateTime autoStartedAt;
    private DateTime autoFinishedAt;

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
        autoTotalMelds = 0;
        autoCompletedMelds = 0;
        autoMateriaConsumed = 0;
        autoStartedAt = default;
        autoFinishedAt = default;
        State = items.Count == 0 ? RunState.Idle : RunState.WaitingForMeldingWindow;
        Status = items.Count == 0 ? "Select at least one item." : "Open Materia Melding, then press Start.";
    }

    public unsafe void PrepareAndOpenMelding(IEnumerable<InventoryGear> selected)
    {
        Load(selected);
        if (items.Count == 0) return;
        if (!services.ClientState.IsLoggedIn) { Fail("You are not logged in."); return; }
        if (services.Condition[ConditionFlag.InCombat]) { Fail("Cannot open Materia Melding in combat."); return; }
        if (!services.GameGui.GetAddonByName("MateriaAttach").IsNull)
        { Status = $"Queue prepared with {items.Count} item(s). Arm and start when ready."; return; }

        var action = services.Data.GetExcelSheet<GeneralAction>()
            .FirstOrDefault(x => x.Name.ExtractText() is "Advanced Materia Melding" or "Materia Melding");
        var actionManager = ActionManager.Instance();
        if (action.RowId == 0 || actionManager == null || !actionManager->UseAction(ActionType.GeneralAction, action.RowId))
        { Fail("Materia Melding could not be opened. Check that the action is unlocked and currently available."); return; }

        State = RunState.WaitingForMeldingWindow;
        Status = $"Queue prepared with {items.Count} item(s); opening Materia Melding...";
    }

    public void Start()
    {
        if (items.Count == 0) { Fail("Prepare a queue with at least one item."); return; }
        if (!services.ClientState.IsLoggedIn) { Fail("You are not logged in."); return; }
        if (services.Condition[ConditionFlag.InCombat]) { Fail("Cannot start in combat."); return; }
        var unsupported = items.FirstOrDefault(x => CurrentMeldCount(x) < 5 && !x.AdvancedMeldingPermitted);
        if (unsupported is not null) { Fail($"{unsupported.Name} cannot be overmelded; remove it from the queue."); return; }
        var liveMeldCounts = items.Select(CurrentMeldCount).ToArray();
        if (liveMeldCounts.Any(x => x < 0)) { Fail("A queued inventory slot no longer contains the expected item."); return; }
        autoItemIndex = Array.FindIndex(liveMeldCounts, x => x < 5);
        if (autoItemIndex < 0) { Fail("Every queued item is already fully melded."); return; }
        if (services.GameGui.GetAddonByName("MateriaAttach").IsNull)
        { State = RunState.WaitingForMeldingWindow; Status = "Open the Materia Melding window."; return; }

        autoCandidateIndex = 0;
        autoRetries = 0;
        autoTotalMelds = liveMeldCounts.Sum(x => Math.Max(0, 5 - x));
        autoCompletedMelds = 0;
        autoMateriaConsumed = 0;
        autoStartedAt = DateTime.UtcNow;
        autoFinishedAt = default;
        autoPhase = AutoPhase.SelectEquipment;
        autoNextAction = DateTime.UtcNow;
        autoDeadline = DateTime.UtcNow.AddSeconds(15);
        State = RunState.Running;
        Status = $"Queue {autoItemIndex + 1}/{items.Count}: starting at slot {items[autoItemIndex].MeldCount + 1} for {items[autoItemIndex].Name}...";
    }

    public void Stop() { autoPhase = AutoPhase.None; advancedPhase = AdvancedPhase.None; autoFinishedAt = DateTime.UtcNow; State = RunState.Idle; Status = "Stopped by user."; }

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
        var expectedGrade = MeldPlan.GradeForSlot(slot, expectedItem.MateriaSlotCount);
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
        if (autoPhase != AutoPhase.None)
        {
            UpdateAutomaticRun();
            return;
        }

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

    private unsafe void UpdateAutomaticRun()
    {
        if (DateTime.UtcNow < autoNextAction) return;
        if (DateTime.UtcNow > autoDeadline)
        {
            if (autoPhase == AutoPhase.WaitDetail && autoRetries < 2)
            {
                autoRetries++;
                services.Log.Warning("Materia detail did not open; retry {Retry}/2 after equipment reselection", autoRetries);
                autoPhase = AutoPhase.SelectEquipment;
                autoNextAction = DateTime.UtcNow.AddSeconds(config.UiCooldownSeconds);
                autoDeadline = DateTime.UtcNow.AddSeconds(20);
                Status = $"Materia UI cooldown retry {autoRetries}/2...";
                return;
            }
            StopAutomaticWithError($"Automatic run timed out in phase {autoPhase}; inspect the item before continuing.");
            return;
        }

        if (autoItemIndex < 0 || autoItemIndex >= items.Count)
        { StopAutomaticWithError("Queue position became invalid; no meld was sent."); return; }

        var expected = items[autoItemIndex];
        var currentMelds = CurrentMeldCount(expected);
        if (currentMelds < 0)
        { StopAutomaticWithError("Queued inventory slot no longer contains the expected item."); return; }

        // Let Monitoring account for the final successful meld and its materia cost
        // before advancing to the next queue item.
        if (currentMelds >= 5 && autoPhase != AutoPhase.Monitoring)
        {
            services.Log.Information("Auto queue item {Index}/{Count} complete: {Item} verified 5/5", autoItemIndex + 1, items.Count, expected.Name);
            do { autoItemIndex++; }
            while (autoItemIndex < items.Count && CurrentMeldCount(items[autoItemIndex]) >= 5);

            if (autoItemIndex >= items.Count)
            {
                autoPhase = AutoPhase.None;
                autoFinishedAt = DateTime.UtcNow;
                State = RunState.Complete;
                Status = $"QUEUE COMPLETE: all {items.Count} selected item(s) verified 5/5.";
                return;
            }

            autoCandidateIndex = 0;
            autoRetries = 0;
            autoPhase = AutoPhase.SelectEquipment;
            autoNextAction = DateTime.UtcNow.AddSeconds(config.UiCooldownSeconds);
            autoDeadline = DateTime.UtcNow.AddSeconds(20);
            Status = $"Queue {autoItemIndex + 1}/{items.Count}: advancing to {items[autoItemIndex].Name}...";
            return;
        }

        switch (autoPhase)
        {
            case AutoPhase.SelectEquipment:
            {
                var main = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttach");
                if (main == null || !main->IsReady) return;
                var rows = FindStringRows(main, 147, expected.Name);
                if (rows.Count != 1)
                { StopAutomaticWithError(rows.Count == 0 ? "Expected equipment is not visible in Materia Melding." : "Duplicate visible equipment names are unsafe in this build."); return; }
                FireInts(main, 1, rows[0] - 147, 1, 0);
                services.Log.Information("Auto phase SelectEquipment: row {Row}, item {Item}", rows[0] - 147, expected.Name);
                autoPhase = AutoPhase.ChooseCandidate;
                autoNextAction = DateTime.UtcNow.AddSeconds(config.UiCooldownSeconds);
                autoDeadline = DateTime.UtcNow.AddSeconds(15);
                Status = $"Queue {autoItemIndex + 1}/{items.Count}: selected {expected.Name}; choosing materia for slot {currentMelds + 1}...";
                break;
            }
            case AutoPhase.ChooseCandidate:
            {
                var slot = currentMelds + 1;
                var grade = MeldPlan.GradeForSlot(slot, expected.MateriaSlotCount);
                if (autoCandidateIndex >= MeldPlan.Priority.Length)
                { StopAutomaticWithError($"No priority materia fits slot {slot} without overcapping."); return; }
                var choice = MeldPlan.Priority[autoCandidateIndex];
                autoMateriaName = MateriaName(choice.Stat, grade);
                pendingMateriaId = grade == 12 ? choice.Grade12ItemId : choice.Grade11ItemId;
                if (CountItem(pendingMateriaId) <= 0)
                { autoCandidateIndex++; return; }

                var main = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttach");
                if (main == null || !main->IsReady) return;
                var rows = FindExactStringRows(main, 429, autoMateriaName);
                if (rows.Count != 1) { autoCandidateIndex++; return; }
                FireInts(main, 2, rows[0] - 429, 1, 0);
                services.Log.Information("Auto phase ChooseCandidate: row {Row}, materia {Materia}", rows[0] - 429, autoMateriaName);
                autoPhase = AutoPhase.WaitDetail;
                autoNextAction = DateTime.UtcNow.AddMilliseconds(200);
                autoDeadline = DateTime.UtcNow.AddSeconds(8);
                Status = $"Checking {autoMateriaName} for slot {slot}...";
                break;
            }
            case AutoPhase.WaitDetail:
            {
                var detail = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttachDialog");
                if (detail == null || !detail->IsReady) return;
                var foundMateria = AtkString(detail, 9);
                var gain = AtkString(detail, 10);
                var foundItem = AtkString(detail, 16);
                var grade = MeldPlan.GradeForSlot(currentMelds + 1, expected.MateriaSlotCount);
                var choice = MeldPlan.Priority[autoCandidateIndex];
                var expectedGain = grade == 12 ? choice.Grade12Gain : choice.Grade11Gain;
                if (foundMateria != autoMateriaName || !foundItem.StartsWith(expected.Name, StringComparison.Ordinal))
                { StopAutomaticWithError("Materia detail identity mismatch; no meld was sent."); return; }
                if (!gain.Contains($"+{expectedGain}", StringComparison.Ordinal))
                {
                    FireInts(detail, 1, 0, 0);
                    services.Log.Information(
                        "Auto rejected {Materia}: displayed gain {Gain}, expected +{ExpectedGain}; reselecting equipment before fallback",
                        autoMateriaName, gain, expectedGain);
                    autoCandidateIndex++;
                    autoPhase = AutoPhase.SelectEquipment;
                    autoNextAction = DateTime.UtcNow.AddSeconds(config.UiCooldownSeconds);
                    autoDeadline = DateTime.UtcNow.AddSeconds(15);
                    Status = $"{autoMateriaName} would overcap; trying the next priority.";
                    return;
                }

                startingMeldCount = currentMelds;
                autoRetries = 0;
                startingMateriaCount = CountItem(pendingMateriaId);
                if (currentMelds < 2)
                {
                    FireInts(detail, 0, 0, 0);
                    autoPhase = AutoPhase.Monitoring;
                    autoDeadline = DateTime.UtcNow.AddSeconds(45);
                    Status = $"Melding slot {currentMelds + 1}: {autoMateriaName}...";
                }
                else
                {
                    FireInts(detail, 0, 0, 1);
                    autoPhase = AutoPhase.WaitYesNo;
                    autoDeadline = DateTime.UtcNow.AddSeconds(10);
                    Status = $"Opening bulk advanced meld for slot {currentMelds + 1}...";
                }
                break;
            }
            case AutoPhase.WaitYesNo:
            {
                var yesno = services.GameGui.GetAddonByName<AtkUnitBase>("SelectYesno");
                if (yesno == null || !yesno->IsReady) return;
                FireInts(yesno, 0);
                autoPhase = AutoPhase.Monitoring;
                autoDeadline = DateTime.UtcNow.AddMinutes(6);
                Status = $"Bulk advanced meld running for slot {startingMeldCount + 1}...";
                break;
            }
            case AutoPhase.Monitoring:
            {
                var count = CountItem(pendingMateriaId);
                if (currentMelds > startingMeldCount)
                {
                    var used = Math.Max(0, startingMateriaCount - count);
                    autoCompletedMelds += currentMelds - startingMeldCount;
                    autoMateriaConsumed += used;
                    Status = $"Slot {currentMelds} succeeded with {autoMateriaName}; {used} consumed.";
                    autoPhase = AutoPhase.WaitReturn;
                    autoNextAction = DateTime.UtcNow.AddSeconds(config.UiCooldownSeconds);
                    autoDeadline = DateTime.UtcNow.AddSeconds(20);
                }
                else if (count <= 0)
                    StopAutomaticWithError("Materia reached zero before success; inspect the item.");
                break;
            }
            case AutoPhase.WaitReturn:
            {
                var detail = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttachDialog");
                if (detail != null && detail->IsReady)
                {
                    FireInts(detail, 1, 0, 0);
                    autoNextAction = DateTime.UtcNow.AddSeconds(config.UiCooldownSeconds);
                    return;
                }
                var main = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaAttach");
                if (main == null || !main->IsReady) return;
                autoCandidateIndex = 0;
                autoPhase = AutoPhase.SelectEquipment;
                autoDeadline = DateTime.UtcNow.AddSeconds(15);
                break;
            }
        }
    }

    private unsafe List<int> FindStringRows(AtkUnitBase* addon, int start, string exact)
    {
        var found = new List<int>();
        for (var i = start; i < addon->AtkValuesCount; i++)
            if (AtkString(addon, i).StartsWith(exact, StringComparison.Ordinal)) found.Add(i);
        return found;
    }

    private unsafe List<int> FindExactStringRows(AtkUnitBase* addon, int start, string exact)
    {
        var found = new List<int>();
        for (var i = start; i < addon->AtkValuesCount; i++)
            if (string.Equals(AtkString(addon, i), exact, StringComparison.Ordinal)) found.Add(i);
        return found;
    }

    private static unsafe string AtkString(AtkUnitBase* addon, int index)
    {
        if (index < 0 || index >= addon->AtkValuesCount) return "";
        var type = addon->AtkValues[index].Type.ToString();
        return type is "String" or "String8" ? addon->AtkValues[index].String.ExtractText().Trim() : "";
    }

    private static unsafe void FireInts(AtkUnitBase* addon, params int[] values)
    {
        var args = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        { args[i].Type = AtkValueType.Int; args[i].Int = values[i]; }
        addon->FireCallback((uint)values.Length, args, true);
    }

    private static string MateriaName(MeldStat stat, int grade) => (stat, grade) switch
    {
        (MeldStat.CriticalHit, 12) => "Savage Aim Materia XII",
        (MeldStat.CriticalHit, _) => "Savage Aim Materia XI",
        (MeldStat.DirectHit, 12) => "Heavens' Eye Materia XII",
        (MeldStat.DirectHit, _) => "Heavens' Eye Materia XI",
        (MeldStat.Determination, 12) => "Savage Might Materia XII",
        _ => "Savage Might Materia XI"
    };

    private void StopAutomaticWithError(string message)
    { autoPhase = AutoPhase.None; autoFinishedAt = DateTime.UtcNow; Fail(message); }

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
    private enum AutoPhase { None, SelectEquipment, ChooseCandidate, WaitDetail, WaitYesNo, Monitoring, WaitReturn }
}
