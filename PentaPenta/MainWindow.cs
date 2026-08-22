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
    private readonly PentameldPricingService pricing;
    private readonly AutoRetainerPricingBridge autoRetainerPricing;
    private readonly RetainerListingScanner retainerListings;
    private List<InventoryGear> gear = [];
    private readonly HashSet<string> selected = [];
    private List<MateriaStock> materiaStock = [];
    private DateTime nextMateriaStockRefresh;
    private string filter = "";
    private bool armFullRun;
    private CraftingMeldPreset? copiedCraftingPreset;
    private string copiedCraftingPresetSource = "";
    private string presetCopyStatus = "";
    private Task<IReadOnlyList<PentameldPriceResult>>? pricingScanTask;
    private IReadOnlyList<PentameldPriceResult> pricingResults = [];
    private string pricingStatus = "Add checked queue items, then refresh prices.";
    private readonly List<PricingCatalogItem> pricingCatalog;
    private string pricingItemSearch = "";
    private bool pricingPickerHq = true;
    private string pricingPickerStatus = "";
    private RetainerListingCapture? retainerCapture;
    private uint? selectedRepriceSlot;
    private bool armSingleReprice;
    private string singleRepriceStatus = "";
    private PendingPriceVerification? pendingPriceVerification;
    private bool armRetainerSweep;
    private bool retainerSweepActive;
    private List<RetainerRepricePlan> retainerSweepPlans = [];
    private int retainerSweepIndex;
    private int retainerSweepChanged;
    private int retainerSweepSkipped;
    private DateTime retainerSweepNextAt;
    private string retainerSweepStatus = "";
    private bool listingAuditActive;
    private readonly Dictionary<string, IReadOnlyList<CapturedRetainerListing>> listingAuditSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(uint ItemId, bool Hq)> listingAuditCharacterItems = [];
    private string listingAuditStatus = "Start a fresh audit, then capture each retainer's Items for Sale window.";
    private bool showCorrectlyPriced;
    private string watchlistFilter = "";
    private string missingItemsFilter = "";

    public MainWindow(Services services, Configuration config, InventoryScanner scanner, MeldController controller, PentameldPricingService pricing, AutoRetainerPricingBridge autoRetainerPricing, RetainerListingScanner retainerListings)
        : base("PentaPenta###PentaPentaMain")
    {
        this.services = services; this.config = config; this.scanner = scanner; this.controller = controller; this.pricing = pricing; this.autoRetainerPricing = autoRetainerPricing; this.retainerListings = retainerListings;
        pricingCatalog = services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            .Where(x => x.EquipSlotCategory.RowId != 0 && x.MateriaSlotCount > 0 && x.IsAdvancedMeldingPermitted)
            .Select(x => new PricingCatalogItem(x.RowId, x.Name.ExtractText()))
            .Where(x => x.Name.Length > 0)
            .OrderBy(x => x.Name)
            .ToList();
        autoRetainerPricing.AutomaticListingAuditStarted += StartAutomaticListingAudit;
        autoRetainerPricing.AutomaticListingAuditCaptured += RecordListingAuditCapture;
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
        if (ImGui.BeginTabItem("Pricing"))
        {
            DrawPentameldPricing();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Recraft Audit"))
        {
            DrawListingCoverageAudit();
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
            foreach (var item in gear.Where(MatchesFilter)) selected.Add(Key(item));
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
            foreach (var item in gear.Where(MatchesFilter))
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

    private void DrawPentameldPricing()
    {
        PollPricingScan();
        PollPendingPriceVerification();
        AdvanceRetainerSweep();
        ImGui.TextWrapped("Compare captured 5/5 retainer listings, review red rows that need attention, then run a manually armed retainer sweep.");
        ImGui.TextDisabled("Competitors must match item and quality with exactly five materia. Universalis prices may be several minutes old.");
        ImGui.Separator();

        var scanRunning = pricingScanTask is { IsCompleted: false };
        if (scanRunning || config.PentameldPricingWatchList.Count == 0) ImGui.BeginDisabled();
        if (ImGui.Button(scanRunning ? "Refreshing..." : "Refresh market prices")) StartPricingScan();
        if (scanRunning || config.PentameldPricingWatchList.Count == 0) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Capture open retainer"))
        {
            retainerCapture = retainerListings.Capture(config.PentameldPricingWatchList);
            RecordListingAuditCapture(retainerCapture);
        }
        ImGui.SameLine();
        ImGui.Checkbox("Show correctly priced", ref showCorrectlyPriced);
        ImGui.TextDisabled(pricingStatus);

        if (ImGui.CollapsingHeader($"Add items & settings ({config.PentameldPricingWatchList.Count} watched)"))
        {
        if (ImGui.Button("Add checked queue items"))
        {
            foreach (var item in gear.Where(x => selected.Contains(Key(x))))
            {
                if (config.PentameldPricingWatchList.Any(x => x.ItemId == item.ItemId && x.Hq == item.Hq)) continue;
                config.PentameldPricingWatchList.Add(new PentameldPricingWatchItem { ItemId = item.ItemId, Name = item.Name, Hq = item.Hq });
            }
            SaveConfig();
        }

        if (ImGui.CollapsingHeader("Add an item without inventory"))
        {
            ImGui.SetNextItemWidth(360);
            ImGui.InputTextWithHint("##pricing-item-search", "Search pentameldable equipment...", ref pricingItemSearch, 100);
            ImGui.SameLine();
            ImGui.Checkbox("HQ", ref pricingPickerHq);
            if (pricingItemSearch.Trim().Length < 2)
            {
                ImGui.TextDisabled("Enter at least two characters.");
            }
            else
            {
                var allMatches = pricingCatalog
                    .Where(x => x.Name.Contains(pricingItemSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (allMatches.Count > 0)
                {
                    if (ImGui.Button($"Add all {allMatches.Count} filtered ({(pricingPickerHq ? "HQ" : "NQ")})"))
                    {
                        var added = 0;
                        foreach (var item in allMatches)
                        {
                            if (config.PentameldPricingWatchList.Any(x => x.ItemId == item.ItemId && x.Hq == pricingPickerHq)) continue;
                            config.PentameldPricingWatchList.Add(new PentameldPricingWatchItem
                                { ItemId = item.ItemId, Name = item.Name, Hq = pricingPickerHq });
                            added++;
                        }
                        if (added > 0) SaveConfig();
                        pricingPickerStatus = $"Added {added} of {allMatches.Count} filtered item(s); duplicates were skipped.";
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled($"Showing the first {Math.Min(30, allMatches.Count)} result(s).");
                }
                var matches = allMatches.Take(30).ToList();
                if (ImGui.BeginTable("pricing-item-picker", 2,
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                        new Vector2(0, 150)))
                {
                    ImGui.TableSetupColumn("Equipment");
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
                    foreach (var item in matches)
                    {
                        ImGui.PushID($"catalog-{item.ItemId}");
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("Add")) AddPricingWatchItem(item.ItemId, item.Name, pricingPickerHq);
                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }
                if (matches.Count == 0) ImGui.TextDisabled("No pentameldable equipment matched that search.");
            }
            if (pricingPickerStatus.Length > 0) ImGui.TextDisabled(pricingPickerStatus);
        }

        var undercut = config.PentameldPricingUndercutGil;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Undercut (gil)", ref undercut))
        {
            config.PentameldPricingUndercutGil = Math.Clamp(undercut, 0, 1_000_000);
            SaveConfig();
        }
        var ownRetainers = config.PentameldPricingOwnRetainers;
        ImGui.SetNextItemWidth(400);
        if (ImGui.InputTextWithHint("Own retainers", "Comma-separated names to exclude", ref ownRetainers, 500))
        {
            config.PentameldPricingOwnRetainers = ownRetainers;
            SaveConfig();
        }
        }

        ImGui.Separator();
        if (retainerCapture is { } capture)
        {
            ImGui.TextWrapped(capture.Status);
            var visibleListings = capture.Listings.Where(listing =>
            {
                var proposal = FindProposal(listing);
                return showCorrectlyPriced || proposal is > 0 && (ulong)proposal.Value != listing.CurrentPrice;
            }).ToList();
            if (!showCorrectlyPriced)
                ImGui.TextDisabled($"Showing {visibleListings.Count} listing(s) needing a price change; {capture.Listings.Count - visibleListings.Count} hidden.");
            if (visibleListings.Count > 0 && ImGui.BeginTable("retainer-listing-capture", 6,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                    new Vector2(0, Math.Min(240, 28 + visibleListings.Count * 24))))
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableSetupColumn("Active retainer listing");
                ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Melds", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Proposed", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();
                foreach (var listing in visibleListings)
                {
                    var proposal = FindProposal(listing);
                    var selected = selectedRepriceSlot == listing.MarketSlot;
                    var canSelect = proposal is > 0 && (ulong)proposal.Value != listing.CurrentPrice;
                    ImGui.TableNextRow();
                    if (canSelect)
                    {
                        var needsChangeColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.08f, 0.08f, 0.42f));
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, needsChangeColor);
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, needsChangeColor);
                    }
                    ImGui.TableNextColumn();
                    if (!canSelect) ImGui.BeginDisabled();
                    if (ImGui.RadioButton($"##reprice-{listing.MarketSlot}", selected)) selectedRepriceSlot = listing.MarketSlot;
                    if (!canSelect) ImGui.EndDisabled();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.Name + (listing.Hq ? " ★" : ""));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted((listing.MarketSlot + 1).ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{listing.MateriaCount}/5");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{listing.CurrentPrice:N0} gil");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatGil(proposal));
                }
                ImGui.EndTable();
            }
            DrawRetainerSweepControls(capture);
            if (ImGui.CollapsingHeader("Advanced / single-item repricing")) DrawSingleRepriceControls(capture);
        }

        if (ImGui.CollapsingHeader("Advanced diagnostics"))
        {
            if (autoRetainerPricing.IsBusy) ImGui.BeginDisabled();
            if (ImGui.Button("Run manual pricing dry test")) autoRetainerPricing.RunManualDryTest();
            if (autoRetainerPricing.IsBusy) ImGui.EndDisabled();
            ImGui.SameLine(); ImGui.TextDisabled("Troubleshooting only; does not invoke AutoRetainer or change prices.");
            ImGui.TextWrapped(autoRetainerPricing.Status);
        }

        if (!ImGui.CollapsingHeader($"Watchlist ({config.PentameldPricingWatchList.Count})")) return;
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##watchlist-filter", "Filter watched items...", ref watchlistFilter, 100);
        uint? removeItemId = null;
        bool removeHq = false;
        if (ImGui.BeginTable("pentameld-pricing", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, 240)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Matches", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Cheapest", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Proposed", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();
            foreach (var watch in config.PentameldPricingWatchList.Where(x => watchlistFilter.Length == 0
                         || x.Name.Contains(watchlistFilter, StringComparison.OrdinalIgnoreCase)))
            {
                var result = pricingResults.FirstOrDefault(x => x.ItemId == watch.ItemId && x.Hq == watch.Hq);
                ImGui.PushID($"pricing-{watch.ItemId}-{watch.Hq}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(watch.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(watch.Hq ? "HQ" : "NQ");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(result is null ? "—" : result.Error is null ? result.QualifyingListings.ToString("N0") : "Error");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatGil(result?.CheapestPrice));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatGil(result?.ProposedPrice));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Remove")) { removeItemId = watch.ItemId; removeHq = watch.Hq; }
                if (result?.Error is { Length: > 0 } error && ImGui.IsItemHovered()) ImGui.SetTooltip(error);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (removeItemId is { } id)
        {
            config.PentameldPricingWatchList.RemoveAll(x => x.ItemId == id && x.Hq == removeHq);
            pricingResults = pricingResults.Where(x => x.ItemId != id || x.Hq != removeHq).ToList();
            SaveConfig();
        }
    }

    private void DrawListingCoverageAudit()
    {
        ImGui.TextWrapped("Compare the complete pricing watchlist with your character bags and every retainer's verified 5/5 sale listings.");
        ImGui.TextDisabled("Automatic cycling is started from AutoRetainer's retainer list with Audit pentameld listings.");
        var auditRetainers = config.PentameldPricingOwnRetainers;
        ImGui.SetNextItemWidth(500);
        if (ImGui.InputTextWithHint("Retainers", "Comma-separated exact retainer names", ref auditRetainers, 500))
        {
            config.PentameldPricingOwnRetainers = auditRetainers;
            SaveConfig();
        }
        ImGui.Separator();
        if (ImGui.Button(listingAuditActive ? "Restart manual audit" : "Start manual audit"))
            StartListingAudit(false);
        ImGui.SameLine();
        if (!listingAuditActive) ImGui.BeginDisabled();
        if (ImGui.Button("Finish audit"))
        {
            listingAuditActive = false;
            autoRetainerPricing.CompleteAutomaticListingAudit();
            listingAuditStatus = "Audit finished. The missing table reflects the captured retainers below.";
        }
        if (!listingAuditActive) ImGui.EndDisabled();
        if (autoRetainerPricing.AutomaticListingAuditActive)
            ImGui.TextWrapped($"AutoRetainer: {autoRetainerPricing.Status}");
        if (ImGui.CollapsingHeader("Manual capture fallback"))
        {
            ImGui.TextDisabled("Open one retainer's Items for Sale window, capture it, then repeat for the remaining retainers.");
            if (ImGui.Button("Capture currently open retainer"))
            {
                retainerCapture = retainerListings.Capture(config.PentameldPricingWatchList);
                RecordListingAuditCapture(retainerCapture);
            }
        }

        var expectedNames = config.PentameldPricingOwnRetainers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var expectedCaptured = expectedNames.Count(x => listingAuditSnapshots.ContainsKey(x));
        ImGui.TextWrapped(listingAuditStatus);
        ImGui.TextDisabled("For automatic cycling, open AutoRetainer's retainer list and click Audit pentameld listings.");
        ImGui.TextUnformatted(expectedNames.Count > 0
            ? $"Retainers captured: {expectedCaptured}/{expectedNames.Count} configured ({listingAuditSnapshots.Count} total snapshots)"
            : $"Retainers captured: {listingAuditSnapshots.Count}. Add own-retainer names above to track audit completeness.");
        if (listingAuditSnapshots.Count > 0)
        {
            ImGui.TextDisabled(string.Join(" · ", listingAuditSnapshots.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value.Count}")));
        }

        var listedPresent = listingAuditSnapshots.Values
            .SelectMany(x => x)
            .Select(x => (x.ItemId, x.Hq))
            .ToHashSet();
        var present = listedPresent.ToHashSet();
        present.UnionWith(listingAuditCharacterItems);
        var missing = config.PentameldPricingWatchList
            .Where(x => !present.Contains((x.ItemId, x.Hq)))
            .OrderBy(x => x.Name)
            .ToList();
        var listedCount = listedPresent.Count;
        var complete = expectedNames.Count > 0 && expectedCaptured == expectedNames.Count;
        if (!complete)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "PRELIMINARY: uncaptured retainers can make items appear missing.");
        ImGui.TextUnformatted($"Watched {config.PentameldPricingWatchList.Count}   |   Listed {listedCount}   |   In bags {listingAuditCharacterItems.Count}   |   Missing {missing.Count}");
        if (missing.Count == 0) return;
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##missing-filter", "Filter missing items...", ref missingItemsFilter, 100);
        var visibleMissing = missing.Where(x => missingItemsFilter.Length == 0
            || x.Name.Contains(missingItemsFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!ImGui.BeginTable("missing-recraft-items", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1))) return;
        ImGui.TableSetupColumn("Missing watched item");
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();
        foreach (var item in visibleMissing)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Hq ? "HQ" : "NQ");
        }
        ImGui.EndTable();
    }

    private void RecordListingAuditCapture(RetainerListingCapture capture)
    {
        if (!listingAuditActive || capture.RetainerName.Length == 0) return;
        listingAuditSnapshots[capture.RetainerName] = capture.Listings.ToList();
        listingAuditStatus = $"Recorded {capture.RetainerName}: {capture.Listings.Count} watched pentamelded listing(s).";
        var expectedNames = config.PentameldPricingOwnRetainers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (autoRetainerPricing.AutomaticListingAuditActive
            && expectedNames.Count > 0
            && expectedNames.All(x => listingAuditSnapshots.ContainsKey(x)))
        {
            listingAuditActive = false;
            autoRetainerPricing.CompleteAutomaticListingAudit();
            listingAuditStatus = $"AUTOMATIC AUDIT COMPLETE: captured all {expectedNames.Count} configured retainers and character inventory.";
        }
    }

    private void StartAutomaticListingAudit() => StartListingAudit(true);

    private void StartListingAudit(bool automatic)
    {
        listingAuditSnapshots.Clear();
        listingAuditCharacterItems.Clear();
        var watched = config.PentameldPricingWatchList.Select(x => (x.ItemId, x.Hq)).ToHashSet();
        foreach (var item in scanner.Scan())
            if (watched.Contains((item.ItemId, item.Hq))) listingAuditCharacterItems.Add((item.ItemId, item.Hq));
        listingAuditActive = true;
        listingAuditStatus = automatic
            ? $"Automatic audit started through AutoRetainer. Character bags contain {listingAuditCharacterItems.Count} watched item type(s)."
            : $"Audit started. Character bags contain {listingAuditCharacterItems.Count} watched item type(s). Open each retainer's Items for Sale window and capture it.";
    }

    private void DrawSingleRepriceControls(RetainerListingCapture capture)
    {
        var selectedListing = selectedRepriceSlot is { } slot
            ? capture.Listings.FirstOrDefault(x => x.MarketSlot == slot)
            : null;
        var proposed = selectedListing is null ? null : FindProposal(selectedListing);
        if (selectedListing is not null)
            ImGui.TextWrapped($"Selected: {selectedListing.Name} — {selectedListing.CurrentPrice:N0} → {FormatGil(proposed)}");

        var busy = pendingPriceVerification is not null || retainerSweepActive;
        if (busy) ImGui.BeginDisabled();
        ImGui.Checkbox("Arm one price change", ref armSingleReprice);
        ImGui.SameLine();
        var canSubmit = armSingleReprice && selectedListing is not null && proposed is > 0;
        if (!canSubmit) ImGui.BeginDisabled();
        if (ImGui.Button("Apply selected price once") && selectedListing is not null && proposed is > 0)
        {
            var submission = retainerListings.SubmitOne(selectedListing, (uint)proposed.Value, config.MaxSingleRepriceDecreasePercent);
            singleRepriceStatus = submission.Status;
            armSingleReprice = false;
            if (submission.Submitted)
                pendingPriceVerification = new PendingPriceVerification(selectedListing, (uint)proposed.Value, DateTime.UtcNow.AddSeconds(10), false);
        }
        if (!canSubmit) ImGui.EndDisabled();
        if (busy) ImGui.EndDisabled();
        if (singleRepriceStatus.Length > 0) ImGui.TextWrapped(singleRepriceStatus);
    }

    private void DrawRetainerSweepControls(RetainerListingCapture capture)
    {
        ImGui.Separator();
        if (retainerSweepActive)
        {
            ImGui.TextWrapped(retainerSweepStatus);
            ImGui.ProgressBar(retainerSweepPlans.Count == 0 ? 0 : retainerSweepIndex / (float)retainerSweepPlans.Count,
                new Vector2(-1, 0), $"{retainerSweepChanged} changed · {retainerSweepSkipped} skipped · {retainerSweepIndex}/{retainerSweepPlans.Count} checked");
            if (ImGui.Button("Stop retainer sweep"))
            {
                retainerSweepActive = false;
                retainerSweepStatus = "Sweep stopped by user; no further prices will be submitted.";
            }
            ImGui.TextDisabled("Keep the Items for Sale window open and do not interact with it during the sweep.");
            return;
        }

        var maxDecrease = config.MaxSingleRepriceDecreasePercent;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Maximum decrease (%)", ref maxDecrease))
        {
            config.MaxSingleRepriceDecreasePercent = Math.Clamp(maxDecrease, 0, 100);
            SaveConfig();
        }
        ImGui.Checkbox("Arm one retainer sweep", ref armRetainerSweep);
        ImGui.SameLine();
        var canStartSweep = armRetainerSweep && pendingPriceVerification is null;
        if (!canStartSweep) ImGui.BeginDisabled();
        if (ImGui.Button("Start captured sweep"))
        {
            StartRetainerSweep(capture);
            armRetainerSweep = false;
        }
        if (!canStartSweep) ImGui.EndDisabled();
        ImGui.TextDisabled("Changes only captured 5/5 watched listings with a valid different proposal; stops on first failure.");
        if (retainerSweepStatus.Length > 0) ImGui.TextWrapped(retainerSweepStatus);
    }

    private void StartRetainerSweep(RetainerListingCapture capture)
    {
        retainerSweepPlans = [];
        retainerSweepSkipped = 0;
        foreach (var listing in capture.Listings)
        {
            var proposed = FindProposal(listing);
            if (proposed is not > 0 || (ulong)proposed.Value == listing.CurrentPrice)
            {
                retainerSweepSkipped++;
                continue;
            }
            retainerSweepPlans.Add(new RetainerRepricePlan(listing, (uint)proposed.Value));
        }
        retainerSweepIndex = 0;
        retainerSweepChanged = 0;
        retainerSweepNextAt = DateTime.UtcNow;
        if (retainerSweepPlans.Count == 0)
        {
            retainerSweepStatus = $"Nothing to change; {retainerSweepSkipped} listing(s) were unchanged or had no proposal.";
            return;
        }
        retainerSweepActive = true;
        retainerSweepStatus = $"Starting one-retainer sweep: {retainerSweepPlans.Count} change(s), {retainerSweepSkipped} initial skip(s).";
    }

    private void AdvanceRetainerSweep()
    {
        if (!retainerSweepActive || pendingPriceVerification is not null || DateTime.UtcNow < retainerSweepNextAt) return;
        if (retainerSweepIndex >= retainerSweepPlans.Count)
        {
            retainerSweepActive = false;
            retainerSweepStatus = $"SWEEP COMPLETE: {retainerSweepChanged} changed, {retainerSweepSkipped} skipped. Reopen Items for Sale to refresh its visible prices.";
            retainerCapture = retainerListings.Capture(config.PentameldPricingWatchList);
            return;
        }

        var plan = retainerSweepPlans[retainerSweepIndex++];
        var liveCapture = retainerListings.Capture(config.PentameldPricingWatchList);
        var liveListing = liveCapture.Listings.FirstOrDefault(x => x.MarketSlot == plan.Listing.MarketSlot);
        if (liveListing is null || liveListing.ItemId != plan.Listing.ItemId || liveListing.Hq != plan.Listing.Hq)
        {
            StopRetainerSweep($"Stopped before slot {plan.Listing.MarketSlot + 1}: listing identity changed.");
            return;
        }
        var submission = retainerListings.SubmitOne(liveListing, plan.ProposedPrice, config.MaxSingleRepriceDecreasePercent);
        if (!submission.Submitted)
        {
            StopRetainerSweep($"Stopped on {plan.Listing.Name}: {submission.Status}");
            return;
        }
        retainerSweepStatus = $"Verifying {plan.Listing.Name}: {liveListing.CurrentPrice:N0} → {plan.ProposedPrice:N0} gil...";
        pendingPriceVerification = new PendingPriceVerification(liveListing, plan.ProposedPrice, DateTime.UtcNow.AddSeconds(10), true);
    }

    private void StopRetainerSweep(string reason)
    {
        retainerSweepActive = false;
        retainerSweepStatus = $"SWEEP STOPPED after {retainerSweepChanged} change(s): {reason}";
    }

    private int? FindProposal(CapturedRetainerListing listing) => autoRetainerPricing.LastResults
        .Concat(pricingResults)
        .FirstOrDefault(x => x.ItemId == listing.ItemId && x.Hq == listing.Hq)?.ProposedPrice;

    private void PollPendingPriceVerification()
    {
        if (pendingPriceVerification is not { } pending) return;
        var livePrice = retainerListings.ReadPrice(pending.Listing);
        if (livePrice == pending.ExpectedPrice)
        {
            var verified = $"Verified: {pending.Listing.Name} is now {livePrice:N0} gil.";
            pendingPriceVerification = null;
            retainerCapture = retainerListings.Capture(config.PentameldPricingWatchList);
            if (pending.IsSweep)
            {
                retainerSweepChanged++;
                retainerSweepStatus = retainerSweepActive
                    ? verified
                    : $"Sweep stopped; the already-submitted final change was verified. {verified}";
                retainerSweepNextAt = DateTime.UtcNow.AddSeconds(1);
            }
            else
            {
                singleRepriceStatus = verified;
            }
            return;
        }
        if (DateTime.UtcNow <= pending.Deadline) return;
        var timeout = livePrice is null
            ? "Price verification timed out because the sale window or item identity changed. Inspect the listing manually."
            : $"Price verification timed out; the live price is {livePrice:N0} gil instead of {pending.ExpectedPrice:N0}. Inspect it manually.";
        pendingPriceVerification = null;
        if (pending.IsSweep) StopRetainerSweep(timeout);
        else singleRepriceStatus = timeout;
    }

    private void StartPricingScan()
    {
        var worldId = services.Objects.LocalPlayer?.HomeWorld.RowId ?? 0;
        if (worldId == 0)
        {
            pricingStatus = "Log in to a character before refreshing prices.";
            return;
        }
        var exclusions = config.PentameldPricingOwnRetainers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = config.PentameldPricingWatchList
            .Select(x => new PentameldPricingWatchItem { ItemId = x.ItemId, Name = x.Name, Hq = x.Hq })
            .ToList();
        pricingStatus = $"Refreshing {snapshot.Count} item(s)...";
        pricingScanTask = pricing.ScanAsync(worldId, snapshot, exclusions, config.PentameldPricingUndercutGil);
    }

    private void AddPricingWatchItem(uint itemId, string name, bool hq)
    {
        if (config.PentameldPricingWatchList.Any(x => x.ItemId == itemId && x.Hq == hq))
        {
            pricingPickerStatus = $"{name} ({(hq ? "HQ" : "NQ")}) is already watched.";
            return;
        }
        config.PentameldPricingWatchList.Add(new PentameldPricingWatchItem { ItemId = itemId, Name = name, Hq = hq });
        SaveConfig();
        pricingPickerStatus = $"Added {name} ({(hq ? "HQ" : "NQ")}).";
    }

    private void PollPricingScan()
    {
        if (pricingScanTask is not { IsCompleted: true } task) return;
        pricingScanTask = null;
        try
        {
            pricingResults = task.GetAwaiter().GetResult();
            var errors = pricingResults.Count(x => x.Error is not null);
            pricingStatus = errors == 0
                ? $"Updated {pricingResults.Count} item(s)."
                : $"Updated with {errors} error(s); hover Error for details.";
        }
        catch (Exception ex)
        {
            pricingStatus = $"Pricing refresh failed: {ex.Message}";
        }
    }

    private void SaveConfig() => services.PluginInterface.SavePluginConfig(config);
    private static string FormatGil(int? value) => value is null ? "—" : $"{value.Value:N0} gil";

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
    private bool MatchesFilter(InventoryGear item) => filter.Length == 0
        || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
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

    private sealed record PricingCatalogItem(uint ItemId, string Name);
    private sealed record PendingPriceVerification(CapturedRetainerListing Listing, uint ExpectedPrice, DateTime Deadline, bool IsSweep);
    private sealed record RetainerRepricePlan(CapturedRetainerListing Listing, uint ProposedPrice);
}
