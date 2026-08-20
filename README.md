# PentaPenta

A Dalamud plugin for selecting pentameldable gear directly from the four player inventory bags and running a guarded sequential queue.

## Current state

- Inventory picker distinguishes duplicate items by container and slot.
- Queue persists across reloads and processes distinct-name items sequentially.
- Default plan is Critical Hit → Direct Hit → Determination.
- Grade XII is used for an item's native slots plus its first overmeld; later slots use grade XI. This means XII in slots 1–3 for normal two-slot gear and slots 1–2 for one-slot accessories.
- Strict no-overcap, inventory identity, combat, login, window, timeout, and materia-quantity gates are enforced.
- Fresh and partially completed items can be queued; completed items are skipped after live 5/5 verification.
- Live grade XI/XII materia inventory counts update during a run and highlight low or empty stock.
- Eligible inventory gear has a **Pentameld** context-menu action that opens PentaPenta with the exact bag/slot selected.
- Preparing a queue opens the Materia Melding window automatically when it is available.
- Overcap rejections are cached per item and grade during a queue so later slots skip choices already proven not to fit.
- Duplicate visible equipment names stop safely because the Materia Melding list does not expose bag/slot identity.

## Build and install

1. Install the current .NET SDK expected by Dalamud.
2. Run `dotnet build PentaPenta.slnx -c Debug`.
3. Add `PentaPenta/bin/Debug/PentaPenta.dll` as a Dalamud dev-plugin location.
4. Enable it and use `/pentapenta`.

Each new version pushed to `main` is built by GitHub Actions and published as a GitHub Release with `latest.zip` and `repo.json`. The permanent custom-repository URL is:

`https://github.com/Elagon-A/Penta-Penta/releases/latest/download/repo.json`

Automation interacts with game UI and can consume materia. Stop immediately on any identity mismatch or unexpected UI state.
