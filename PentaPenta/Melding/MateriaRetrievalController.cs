using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PentaPenta.Models;

namespace PentaPenta.Melding;

internal sealed class MateriaRetrievalController : IDisposable
{
    private readonly Services services;
    private InventoryGear? item;
    private RetrievalPhase phase;
    private DateTime deadline;
    private int startingMeldCount;
    private string materiaName = "";

    internal string Status { get; private set; } = "Prepare one selected melded item, then open Retrieve Materia for it.";
    internal bool IsRunning => phase != RetrievalPhase.None;

    internal MateriaRetrievalController(Services services)
    {
        this.services = services;
        services.Framework.Update += OnFrameworkUpdate;
    }

    internal void Prepare(IEnumerable<InventoryGear> selected)
    {
        if (IsRunning) { Status = "Stop the active retrieval test first."; return; }
        var candidates = selected.Where(x => CurrentMeldCount(x) > 0).ToList();
        if (candidates.Count != 1)
        {
            item = null;
            Status = "Select exactly one item that currently contains materia for the guarded test.";
            return;
        }

        item = candidates[0];
        Status = $"Prepared {item.Name} at bag slot {item.Slot + 1}. Open Retrieve Materia for this exact item.";
    }

    internal unsafe void ExecuteOneVerified()
    {
        if (item is null) { Status = "Prepare exactly one melded item first."; return; }
        if (IsRunning) { Status = "A retrieval test is already running."; return; }
        if (!services.ClientState.IsLoggedIn || services.Condition[ConditionFlag.InCombat])
        { Status = "Retrieval cannot start while logged out or in combat."; return; }

        var liveMelds = CurrentMeldCount(item);
        if (liveMelds <= 0) { Status = "The prepared inventory slot is missing or has no materia."; return; }
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>("MateriaRetrieveDialog");
        if (addon == null || !addon->IsReady || addon->AtkValuesCount < 30)
        { Status = "Open the Materia Retrieval dialog for the prepared item first."; return; }

        var displayedItem = AtkString(addon, 3);
        var rawItemId = addon->AtkValues[0].Int;
        var expectedRawId = checked((int)item.ItemId + (item.Hq ? 1_000_000 : 0));
        if (!displayedItem.StartsWith(item.Name, StringComparison.Ordinal) || rawItemId != expectedRawId)
        { Status = $"Retrieval dialog mismatch; expected {item.Name}. No callback sent."; return; }
        if (addon->AtkValues[6].Int != liveMelds)
        { Status = "Retrieval dialog meld count does not match the inventory. No callback sent."; return; }

        materiaName = AtkString(addon, 13);
        if (string.IsNullOrWhiteSpace(materiaName) || !materiaName.Contains("Materia", StringComparison.Ordinal))
        { Status = "Could not verify the first displayed materia. No callback sent."; return; }

        startingMeldCount = liveMelds;
        FireInts(addon, 0, 0, 0);
        phase = RetrievalPhase.WaitConfirmation;
        deadline = DateTime.UtcNow.AddSeconds(10);
        Status = $"Requested one retrieval of {materiaName}; waiting for the game confirmation...";
    }

    internal void Stop()
    {
        phase = RetrievalPhase.None;
        Status = "Retrieval test stopped by user.";
    }

    private unsafe void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (phase == RetrievalPhase.None || item is null) return;
        var liveMelds = CurrentMeldCount(item);
        if (liveMelds < 0) { Fail("The prepared inventory slot changed; retrieval stopped."); return; }
        if (liveMelds < startingMeldCount)
        {
            phase = RetrievalPhase.None;
            Status = $"Verified: one materia was removed from {item.Name} ({startingMeldCount} → {liveMelds}).";
            return;
        }

        if (phase == RetrievalPhase.WaitConfirmation)
        {
            var yesno = services.GameGui.GetAddonByName<AtkUnitBase>("SelectYesno");
            if (yesno != null && yesno->IsReady)
            {
                var text = AllStrings(yesno);
                if (!text.Contains(materiaName, StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("retrieve", StringComparison.OrdinalIgnoreCase))
                { Fail("An unrelated confirmation window appeared; no confirmation was sent."); return; }
                FireInts(yesno, 0);
                phase = RetrievalPhase.Monitoring;
                deadline = DateTime.UtcNow.AddSeconds(15);
                Status = $"Confirmed one retrieval of {materiaName}; verifying inventory...";
                return;
            }
        }

        if (DateTime.UtcNow > deadline)
            Fail($"Retrieval timed out with the item unchanged at {liveMelds} materia. No retry was sent.");
    }

    private int CurrentMeldCount(InventoryGear expected)
    {
        var slots = services.Inventory.GetInventoryItems(expected.Container);
        if (expected.Slot >= slots.Length) return -1;
        var live = slots[(int)expected.Slot];
        if (live.IsEmpty || live.BaseItemId != expected.ItemId || live.IsHq != expected.Hq) return -1;
        return live.MateriaEntries.Count(x => x.Type.RowId != 0);
    }

    private static unsafe string AllStrings(AtkUnitBase* addon)
    {
        var values = new List<string>();
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var text = AtkString(addon, i);
            if (text.Length > 0) values.Add(text);
        }
        return string.Join(" ", values);
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

    private void Fail(string message)
    {
        phase = RetrievalPhase.None;
        Status = message;
        services.Log.Warning(message);
    }

    public void Dispose() => services.Framework.Update -= OnFrameworkUpdate;

    private enum RetrievalPhase { None, WaitConfirmation, Monitoring }
}
