# PentaPenta

A Dalamud plugin for selecting pentameldable gear directly from the four player inventory bags and running a guarded sequential queue.

## Current state

- Inventory picker distinguishes duplicate items by container and slot.
- Queue persists across reloads and processes distinct-name items sequentially.
- Default plan is Critical Hit → Direct Hit → Determination.
- Slots 1–3 use grade XII; slots 4–5 use grade XI.
- Strict no-overcap, inventory identity, combat, login, window, timeout, and materia-quantity gates are enforced.
- Fresh and partially completed items can be queued; completed items are skipped after live 5/5 verification.
- Duplicate visible equipment names stop safely because the Materia Melding list does not expose bag/slot identity.

## Build and install

1. Install the current .NET SDK expected by Dalamud.
2. Run `dotnet build PentaPenta.slnx -c Debug`.
3. Add `PentaPenta/bin/Debug/PentaPenta.dll` as a Dalamud dev-plugin location.
4. Enable it and use `/pentapenta`.

Each new version pushed to `main` is built by GitHub Actions and published as a GitHub Release with `latest.zip` and `repo.json`. The permanent custom-repository URL is:

`https://github.com/Elagon-A/Penta-Penta/releases/latest/download/repo.json`

Automation interacts with game UI and can consume materia. Stop immediately on any identity mismatch or unexpected UI state.
