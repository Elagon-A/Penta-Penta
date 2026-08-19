using Dalamud.Game.ClientState.Conditions;
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
    private void Fail(string message) { State = RunState.Error; Status = message; services.Log.Warning(message); }
}
