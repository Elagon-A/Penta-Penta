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
        if (ImGui.BeginTabItem("Materia History"))
        {
            DrawMateriaHistory();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Pentameld Pricing"))
        {
            DrawPentameldPricing();
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
        ImGui.TextWrapped("Read-only market comparison. A qualifying competitor is the same item and quality with exactly five materia; materia types and grades may differ.");
        ImGui.TextDisabled("This tab does not invoke AutoRetainer or change any sale price. Universalis data may be several minutes old.");
        ImGui.Separator();

        if (ImGui.Button("Add checked queue items"))
        {
            foreach (var item in gear.Where(x => selected.Contains(Key(x))))
            {
                if (config.PentameldPricingWatchList.Any(x => x.ItemId == item.ItemId && x.Hq == item.Hq)) continue;
                config.PentameldPricingWatchList.Add(new PentameldPricingWatchItem { ItemId = item.ItemId, Name = item.Name, Hq = item.Hq });
            }
            SaveConfig();
        }
        ImGui.SameLine();
        var scanRunning = pricingScanTask is { IsCompleted: false };
        if (scanRunning || config.PentameldPricingWatchList.Count == 0) ImGui.BeginDisabled();
        if (ImGui.Button(scanRunning ? "Refreshing..." : "Refresh prices")) StartPricingScan();
        if (scanRunning || config.PentameldPricingWatchList.Count == 0) ImGui.EndDisabled();

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
        ImGui.TextDisabled(pricingStatus);

        ImGui.Separator();
        if (ImGui.Button("Capture open retainer listings"))
            retainerCapture = retainerListings.Capture(config.PentameldPricingWatchList);
        ImGui.SameLine();
        ImGui.TextDisabled("Read only; requires the retainer Items for Sale window.");
        if (retainerCapture is { } capture)
        {
            ImGui.TextWrapped(capture.Status);
            if (capture.Listings.Count > 0 && ImGui.BeginTable("retainer-listing-capture", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                    new Vector2(0, Math.Min(180, 28 + capture.Listings.Count * 24))))
            {
                ImGui.TableSetupColumn("Active retainer listing");
                ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Melds", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Proposed", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();
                foreach (var listing in capture.Listings)
                {
                    var proposal = autoRetainerPricing.LastResults
                        .Concat(pricingResults)
                        .FirstOrDefault(x => x.ItemId == listing.ItemId && x.Hq == listing.Hq)?.ProposedPrice;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(listing.Name + (listing.Hq ? " ★" : ""));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted((listing.MarketSlot + 1).ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{listing.MateriaCount}/5");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{listing.CurrentPrice:N0} gil");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatGil(proposal));
                }
                ImGui.EndTable();
            }
        }

        ImGui.Separator();
        var dryRun = config.EnableAutoRetainerPricingDryRun;
        if (ImGui.Checkbox("Enable AutoRetainer pricing dry run", ref dryRun))
        {
            config.EnableAutoRetainerPricingDryRun = dryRun;
            SaveConfig();
            autoRetainerPricing.ConfigurationChanged();
        }
        ImGui.TextDisabled("During AutoRetainer post-processing, calculate proposals for watched items without changing prices.");
        if (autoRetainerPricing.IsBusy) ImGui.BeginDisabled();
        if (ImGui.Button("Run dry test now")) autoRetainerPricing.RunManualDryTest();
        if (autoRetainerPricing.IsBusy) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Does not require or invoke AutoRetainer.");
        ImGui.TextWrapped($"AutoRetainer: {autoRetainerPricing.Status}");
        if (autoRetainerPricing.LastResults.Count > 0)
        {
            if (ImGui.BeginTable("autoretainer-dry-run", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn($"Last retainer: {autoRetainerPricing.LastRetainer}");
                ImGui.TableSetupColumn("Matches", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Proposed", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();
                foreach (var result in autoRetainerPricing.LastResults)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(result.Name + (result.Hq ? " ★" : ""));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(result.Error is null ? result.QualifyingListings.ToString("N0") : "Error");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatGil(result.ProposedPrice));
                }
                ImGui.EndTable();
            }
        }

        uint? removeItemId = null;
        bool removeHq = false;
        if (ImGui.BeginTable("pentameld-pricing", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, -1)))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Matches", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Cheapest", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Proposed", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();
            foreach (var watch in config.PentameldPricingWatchList)
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
}
