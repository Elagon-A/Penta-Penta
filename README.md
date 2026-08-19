# PentaPenta

A Dalamud plugin prototype for selecting pentameldable gear directly from the four player inventory bags and preparing a guarded batch queue.

## Current state

- Inventory picker distinguishes duplicate items by container and slot.
- Queue persists across reloads.
- Default plan is Critical Hit → Direct Hit → Determination.
- Slots 1–3 use grade XII; slots 4–5 use grade XI.
- Strict no-overcap and combat/login/window gates are modeled.
- The final native `MateriaAttach` callback driver is intentionally disabled until its row and callback map is validated against the current game patch. This prevents an unverified development build from consuming materia on the wrong item.

## Build and install

1. Install the current .NET SDK expected by Dalamud.
2. Run `dotnet build PentaPenta.slnx -c Debug`.
3. Add `PentaPenta/bin/Debug/PentaPenta.dll` as a Dalamud dev-plugin location.
4. Enable it and use `/pentapenta`.

Do not test automation with valuable materia. Start with a disposable item and stop immediately on any identity mismatch.
