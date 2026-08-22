# PentaPenta

A Dalamud plugin for selecting pentameldable gear directly from the four player inventory bags and running a guarded sequential queue.

## Current state

- Inventory picker distinguishes duplicate items by container and slot.
- `Select all` makes the queue match the active inventory filter exactly; with no filter it selects every eligible item.
- Queue persists across reloads and processes distinct-name items sequentially.
- Default plan is Critical Hit → Direct Hit → Determination.
- DoH equipment without an enabled exact preset defaults to Craftsmanship → CP → Control; exact five-slot presets still take precedence.
- Grade XII is used for an item's native slots plus its first overmeld; later slots use grade XI. This means XII in slots 1–3 for normal two-slot gear and slots 1–2 for one-slot accessories.
- Strict no-overcap, inventory identity, combat, login, window, timeout, and materia-quantity gates are enforced.
- A stalled queue automatically reconciles the live item state and retries safely up to three times before stopping for inspection.
- Bulk overmeld recovery shows live materia consumption and extends its wait while attempts are progressing.
- Fast bulk recovery uses an 8-second quiet threshold and two inventory snapshots one second apart before retrying; any resumed activity cancels the retry.
- Exact crafting presets can be disabled temporarily without erasing their saved five-slot plan.
- Exact crafting presets allow a positive partial stat gain when the selected materia intentionally reaches an item's cap; zero-gain melds are still rejected.
- A nearby-marketboard overlay shows grade XI/XII battle and crafting materia stock and opens a clicked materia's native market listings.
- The nearby-marketboard overlay can be enabled or disabled from PentaPenta's main window, and the setting is saved.
- One-click market searches wait for the native marketboard controls to finish initializing before entering and selecting the requested materia.
- One-click market searches focus and activate the native text field before submitting, matching the game's search-button enablement sequence.
- Market searches dispatch the text field's native click event to leave category mode, then wait until the game's Search button reports enabled.
- Native market text-field clicks resolve through the input collision node used by the current game UI, with an owner-node fallback.
- Text searches explicitly clear the retained category filter and report phase-specific timeout errors.
- Programmatic market text entry invokes the field's native TextChanged callback so the game enables Search exactly as it does for typed input.
- Market search automation avoids unvalidated global-focus and raw button-event paths that can destabilize the game client.
- Market search diagnostic mode records the safe addon and search-agent transitions produced by one manual field click and Search press.
- Diagnostic searches ignore cached agent results and advance only after the current Item Search window receives a new visible result list.
- Callback diagnostics use Dalamud's supported addon lifecycle listener to record the native event type and parameter from manual Item Search interactions without replaying pointers.
- Marketboard safe mode populates the search text but requires manual field activation and Search; synthetic focus callbacks remain disabled because they require native event data.
- The shopping overlay uses compact Battle/Crafting tables where the live XI/XII inventory quantities are the listing buttons.
- A complete exact crafting preset can be copied, then applied as independent saved presets to every checked queue item type.
- Fresh and partially completed items can be queued; completed items are skipped after live 5/5 verification.
- Live grade XI/XII materia inventory counts update during a run and highlight low or empty stock.
- A persistent Materia History tab totals consumption by materia type and preserves the statistics across updates and restarts.
- A read-only Pentameld Pricing tab scans watched items against same-world Universalis listings, filters to matching HQ/NQ listings with exactly five materia, excludes configured own-retainer names, and proposes a configurable gil undercut.
- An opt-in AutoRetainer dry-run hook uses AutoRetainer's supported retainer post-process handshake, calculates watched-item proposals for each processed retainer, and releases the post-process slot without changing sale prices.
- The same AutoRetainer pricing dry run can be started manually at any time without invoking AutoRetainer or waiting for a completed venture.
- A searchable equipment picker can add HQ or NQ pentameldable items directly to the pricing watchlist even when they are not in the player's inventory.
- The pricing picker can add every item matching its active text filter at once, applying the selected HQ/NQ quality and skipping existing watch entries.
- A read-only active-retainer capture reads the loaded Items for Sale inventory, verifies watched item identity, quality, and 5/5 materia, and displays current versus proposed prices without clicking the retainer UI.
- A manually armed single-listing repricing test revalidates the open sale window, market slot, item identity, quality, 5/5 materia, unchanged current price, valid proposal, and maximum decrease before submitting one price and reading it back for confirmation.
- A manually armed one-retainer sweep processes captured changed proposals one at a time, verifies every write before continuing, waits between submissions, skips unchanged or missing proposals, and stops on the first mismatch, rejection, or timeout.
- The active single-listing radio selection highlights its full captured retainer row in red for visibility before an armed price change.
- Eligible inventory gear has a **Pentameld** context-menu action that opens PentaPenta with the exact bag/slot selected.
- Preparing a queue opens the Materia Melding window automatically when it is available.
- Overcap rejections are cached per item and grade during a queue so later slots skip choices already proven not to fit.
- Exact crafting presets can assign Craftsmanship, Control, or CP materia in grade XI/XII to each of an item's five slots.
- Duplicate visible equipment names stop safely because the Materia Melding list does not expose bag/slot identity.

## Build and install

1. Install the current .NET SDK expected by Dalamud.
2. Run `dotnet build PentaPenta.slnx -c Debug`.
3. Add `PentaPenta/bin/Debug/PentaPenta.dll` as a Dalamud dev-plugin location.
4. Enable it and use `/pentapenta`.

Each new version pushed to `main` is built by GitHub Actions and published as a GitHub Release with `latest.zip` and `repo.json`. The permanent custom-repository URL is:

`https://github.com/Elagon-A/Penta-Penta/releases/latest/download/repo.json`

Automation interacts with game UI and can consume materia. Stop immediately on any identity mismatch or unexpected UI state.
