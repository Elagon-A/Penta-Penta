using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace PentaPenta;

internal sealed class MarketBoardOverlay : Window, IDisposable
{
    private const uint MarketBoardDataId = 2000442;
    private static readonly MarketMateriaRow[] BattleMateria =
    [
        new("Critical Hit", 41772, 41759),
        new("Direct Hit", 41771, 41758),
        new("Determination", 41773, 41760),
    ];
    private static readonly MarketMateriaRow[] CraftingMateria =
    [
        new("Craftsmanship", 41778, 41765),
        new("Control", 41780, 41767),
        new("CP", 41779, 41766),
    ];
    private static readonly MarketMateriaRow[] GatheringMateria =
    [
        new("Gathering", 41775, 41762),
        new("Perception", 41776, 41763),
        new("GP", 41777, 41764),
    ];

    private readonly Services services;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private uint pendingItemId;
    private string pendingItemName = "";
    private DateTime pendingDeadline;
    private DateTime nextPendingAction;
    private PendingPhase pendingPhase;
    private readonly List<uint> batchAuditItems = [];
    private int batchAuditIndex;
    private bool automaticSearch;
    private DateTime nextStockRefresh;
    private string lastDiagnosticSnapshot = "";
    private int diagnosticInitialVisibleResults;
    private Dictionary<uint, int> stock = [];
    private string status = "Click a materia to open its market listings.";
    private bool wasNearMarketBoard;

    internal bool IsBatchAuditRunning => batchAuditItems.Count > 0;
    internal string BatchAuditStatus { get; private set; } = "No native retainer batch scan has been run.";
    internal Func<uint, bool>? CaptureAuditListing { private get; set; }
    internal Action<RetainerListingCapture>? CaptureRetainerForAudit { private get; set; }

    public MarketBoardOverlay(Services services, Configuration config, InventoryScanner scanner)
        : base("PentaPenta Materia Shopping###PentaPentaMarket")
    {
        this.services = services;
        this.config = config;
        this.scanner = scanner;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 300),
            MaximumSize = new Vector2(560, 520),
        };
        IsOpen = false;
        services.Framework.Update += OnFrameworkUpdate;
        services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "ItemSearch", OnItemSearchReceiveEvent);
    }

    public override bool DrawConditions()
        => config.EnableMarketBoardOverlay && services.ClientState.IsLoggedIn && FindNearbyMarketBoard() is not null;

    public override void Draw()
    {
        RefreshStockIfDue();
        ImGui.TextUnformatted("Click a quantity to view listings.");
        DrawMateriaSection("Battle", BattleMateria);
        DrawMateriaSection("Crafting", CraftingMateria);
        DrawMateriaSection("Gathering", GatheringMateria);
        ImGui.Separator();
        ImGui.TextDisabled(status);
        if (IsBatchAuditRunning)
        {
            ImGui.Separator();
            ImGui.TextWrapped(BatchAuditStatus);
            if (ImGui.Button("Stop native price scan")) StopBatchAudit();
        }
    }

    private void DrawMateriaSection(string title, IReadOnlyList<MarketMateriaRow> rows)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        if (!ImGui.BeginTable($"market-{title}", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) return;
        ImGui.TableSetupColumn("Materia");
        ImGui.TableSetupColumn("XII", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("XI", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableHeadersRow();
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Stat);
            ImGui.TableNextColumn(); DrawStockButton(row.Grade12ItemId);
            ImGui.TableNextColumn(); DrawStockButton(row.Grade11ItemId);
        }
        ImGui.EndTable();
    }

    private void DrawStockButton(uint itemId)
    {
        var count = stock.GetValueOrDefault(itemId);
        var color = StockColor(count);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushID((int)itemId);
        if (ImGui.Button(count.ToString("N0"), new Vector2(-1, 0))) QueueListing(itemId);
        if (ImGui.IsItemHovered())
        {
            var name = ResolveItemName(itemId);
            ImGui.SetTooltip($"Open {name} listings");
        }
        ImGui.PopID();
        ImGui.PopStyleColor();
    }

    private unsafe void QueueListing(uint itemId, bool automatic = false)
    {
        automaticSearch = automatic;
        pendingItemId = itemId;
        pendingItemName = ResolveItemName(itemId);
        if (pendingItemName.Length == 0)
        {
            CancelPending($"Could not resolve materia item {itemId}.");
            return;
        }
        pendingDeadline = DateTime.UtcNow.AddSeconds(12);

        if (IsMarketSearchReady())
        {
            RunNativeSearch();
            return;
        }

        // The addon can exist for several frames before its controls are ready.
        // Wait for initialization instead of failing or interacting a second time.
        if (!services.GameGui.GetAddonByName("ItemSearch").IsNull)
        {
            pendingPhase = PendingPhase.OpeningBoard;
            status = $"Waiting for the marketboard to finish opening for {pendingItemName}...";
            return;
        }

        var board = FindNearbyMarketBoard();
        var targetSystem = TargetSystem.Instance();
        if (board is null || targetSystem == null)
        {
            CancelPending("The nearby marketboard could not be reached.");
            return;
        }

        targetSystem->InteractWithObject((GameObject*)board.Address, false);
        pendingPhase = PendingPhase.OpeningBoard;
        status = $"Opening the marketboard for {pendingItemName}...";
    }

    internal bool OpenListingForAudit(uint itemId)
    {
        if (pendingItemId != 0)
        {
            status = $"Finish the current market operation for {pendingItemName} first.";
            return false;
        }
        QueueListing(itemId);
        return pendingItemId == itemId;
    }

    internal bool StartBatchAudit(RetainerListingCapture retainerCapture)
    {
        if (pendingItemId != 0 || IsBatchAuditRunning)
        {
            BatchAuditStatus = "Finish or stop the current market operation first.";
            return false;
        }
        if (!services.GameGui.GetAddonByName("ItemSearchResult").IsNull)
        {
            BatchAuditStatus = "Close the existing Search Results window before starting the native batch scan.";
            return false;
        }

        var unique = retainerCapture.Listings.Select(x => x.ItemId).Where(x => x != 0).Distinct().ToList();
        if (unique.Count == 0)
        {
            BatchAuditStatus = "The open retainer has no watched 5/5 listings to scan.";
            return false;
        }
        if (!IsMarketSearchReady() && FindNearbyMarketBoard() is null)
        {
            BatchAuditStatus = "Move within 7 yalms of a marketboard before starting the native batch scan.";
            return false;
        }

        CaptureRetainerForAudit?.Invoke(retainerCapture);
        batchAuditItems.AddRange(unique);
        batchAuditIndex = 0;
        BatchAuditStatus = $"Starting native scan: 0/{batchAuditItems.Count}. Do not interact with the market windows.";
        StartNextBatchItem();
        return IsBatchAuditRunning;
    }

    internal void StopBatchAudit(string reason = "Stopped by user.")
    {
        var total = batchAuditItems.Count;
        batchAuditItems.Clear();
        pendingItemId = 0;
        pendingItemName = "";
        pendingPhase = PendingPhase.None;
        automaticSearch = false;
        BatchAuditStatus = $"Native scan stopped at {batchAuditIndex}/{total}: {reason}";
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!config.EnableMarketBoardOverlay && !IsBatchAuditRunning && pendingItemId == 0)
        {
            IsOpen = false;
            wasNearMarketBoard = false;
            return;
        }

        var isNearby = FindNearbyMarketBoard() is not null;
        if (config.EnableMarketBoardOverlay)
        {
            if (isNearby && !wasNearMarketBoard) IsOpen = true;
            if (!isNearby) IsOpen = false;
            wasNearMarketBoard = isNearby;
        }
        else
        {
            // Native pricing scans share the market-search controller but are not
            // part of the optional materia-shopping window.
            IsOpen = false;
            wasNearMarketBoard = false;
        }

        if (pendingItemId == 0)
        {
            if (IsBatchAuditRunning && DateTime.UtcNow >= nextPendingAction)
                StartNextBatchItem();
            return;
        }
        if (DateTime.UtcNow > pendingDeadline)
        {
            var timeoutMessage = pendingPhase switch
            {
                PendingPhase.OpeningBoard => automaticSearch
                    ? "Native price scan could not open the marketboard. Move closer and retry the scan."
                    : "Marketboard opening timed out. Move closer and click the materia again.",
                PendingPhase.FocusingSearch => "Marketboard did not enter text-search mode or enable Search.",
                PendingPhase.WaitingForSearchResults => "Marketboard item search returned no exact result before timing out.",
                PendingPhase.WaitingForListings => "The selected materia's listings did not open before timing out.",
                PendingPhase.WaitingForCapture => "Native market results did not become readable before timing out.",
                PendingPhase.DiagnosticManualSearch => "Automatic market search produced no fresh visible results.",
                _ => "Marketboard operation timed out.",
            };
            CancelPending(timeoutMessage);
            return;
        }
        if (pendingPhase == PendingPhase.OpeningBoard && IsMarketSearchReady())
        {
            RunNativeSearch();
            return;
        }
        if (pendingPhase == PendingPhase.DiagnosticManualSearch)
        {
            ObserveManualSearchDiagnostic();
            return;
        }
        if (pendingPhase == PendingPhase.FocusingSearch)
        {
            if (DateTime.UtcNow < nextPendingAction) return;
            SubmitNativeSearch();
            return;
        }
        if (pendingPhase == PendingPhase.WaitingForSearchResults)
        {
            SelectExactSearchResult();
            return;
        }
        if (pendingPhase == PendingPhase.WaitingForListings
            && !services.GameGui.GetAddonByName("ItemSearchResult").IsNull)
        {
            if (automaticSearch)
            {
                pendingPhase = PendingPhase.WaitingForCapture;
                nextPendingAction = DateTime.UtcNow.AddMilliseconds(1200);
                pendingDeadline = DateTime.UtcNow.AddSeconds(10);
                status = $"Verifying listings for {pendingItemName}...";
                return;
            }
            status = $"Opened listings for {pendingItemName}.";
            pendingItemId = 0;
            pendingItemName = "";
            pendingPhase = PendingPhase.None;
        }
        if (pendingPhase == PendingPhase.WaitingForCapture && DateTime.UtcNow >= nextPendingAction)
            FinishBatchItemCapture();
    }

    private unsafe void RunNativeSearch()
    {
        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        if (addon == null || !addon->IsReady || addon->SearchTextInput == null || addon->ResultsList == null)
        {
            CancelPending("The native market search window was not ready. Try the item again.");
            return;
        }

        var inputBase = (AtkComponentBase*)addon->SearchTextInput;
        var inputNode = inputBase->OwnerNode;
        if (inputNode == null)
        {
            CancelPending("The native market search field had no input node.");
            return;
        }

        // Reproduce the field's real mouse-click event. This switches Item Search
        // out of the selected category (for example Gladiator's Arms) and into
        // text/partial-match mode, which is what enables the Search button.
        var textInputBase = (AtkComponentInputBase*)addon->SearchTextInput;
        var collisionNode = textInputBase->CollisionNode;
        var clickEvent = collisionNode == null
            ? null
            : (AtkEvent*)collisionNode->AtkResNode.AtkEventManager.Event;
        while (clickEvent != null && clickEvent->State.EventType != AtkEventType.MouseClick)
            clickEvent = clickEvent->NextEvent;
        if (clickEvent == null)
        {
            clickEvent = (AtkEvent*)inputNode->AtkResNode.AtkEventManager.Event;
            while (clickEvent != null && clickEvent->State.EventType != AtkEventType.MouseClick)
                clickEvent = clickEvent->NextEvent;
        }
        if (clickEvent == null)
        {
            CancelPending("The native market search field had no click event.");
            return;
        }
        addon->ReceiveEvent(clickEvent->State.EventType, (int)clickEvent->Param, clickEvent);
        addon->SetFocusNode((AtkResNode*)inputNode, true, 0);
        addon->SearchTextInput->IsActive = true;
        addon->SearchTextInput->SetText(pendingItemName);
        addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, -1);
        addon->Mode = AddonItemSearch.SearchMode.Normal;
        addon->SelectedFilter = -1;
        addon->SearchText.SetString(pendingItemName);
        addon->SearchText2.SetString(pendingItemName);
        if (textInputBase->Callback == null)
        {
            CancelPending("The native market search field had no text-change callback.");
            return;
        }
        var textChanged = (delegate* unmanaged<AtkUnitBase*, InputCallbackType, CStringPointer,
            CStringPointer, int, InputCallbackResult>)textInputBase->Callback;
        textChanged((AtkUnitBase*)addon, InputCallbackType.TextChanged,
            textInputBase->RawString.StringPtr, textInputBase->EvaluatedString.StringPtr,
            textInputBase->CallbackEventKind);
        if (automaticSearch)
        {
            pendingPhase = PendingPhase.FocusingSearch;
            nextPendingAction = DateTime.UtcNow.AddMilliseconds(350);
            pendingDeadline = DateTime.UtcNow.AddSeconds(12);
            status = $"Preparing native search for {pendingItemName}...";
            return;
        }

        // Pause for two real clicks while the supported AddonLifecycle service
        // records the callback type/parameter generated by the current client.
        pendingPhase = PendingPhase.DiagnosticManualSearch;
        pendingDeadline = DateTime.UtcNow.AddMinutes(2);
        lastDiagnosticSnapshot = "";
        diagnosticInitialVisibleResults = addon->ResultsList->GetItemCount();
        LogDiagnosticSnapshot("text populated");
        status = $"SAFE MODE: click the search field, then Search for {pendingItemName}.";
    }

    private unsafe void ObserveManualSearchDiagnostic()
    {
        LogDiagnosticSnapshot("state changed");

        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentItemSearch*)agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        var visibleResults = addon == null || !addon->IsReady || addon->ResultsList == null
            ? -1
            : addon->ResultsList->GetItemCount();
        if (agent == null || agent->ItemBuffer == null || agent->ItemCount == 0
            || visibleResults <= diagnosticInitialVisibleResults) return;

        services.Log.Information("[MarketDiagnostic] Native search produced {Count} result(s); selecting exact item {ItemId}.",
            agent->ItemCount, pendingItemId);
        pendingPhase = PendingPhase.WaitingForSearchResults;
        pendingDeadline = DateTime.UtcNow.AddSeconds(12);
        status = $"DIAGNOSTIC captured; opening exact result for {pendingItemName}...";
        SelectExactSearchResult();
    }

    private unsafe void LogDiagnosticSnapshot(string reason)
    {
        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentItemSearch*)agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (addon == null || !addon->IsReady || addon->SearchTextInput == null || addon->SearchButton == null) return;

        var snapshot = $"mode={addon->Mode}; filter={addon->SelectedFilter}; "
            + $"inputActive={addon->SearchTextInput->IsActive}; searchEnabled={addon->SearchButton->IsEnabled}; "
            + $"results={(addon->ResultsList == null ? -1 : addon->ResultsList->GetItemCount())}; "
            + $"agentItems={(agent == null ? -1 : (int)agent->ItemCount)}";
        if (snapshot == lastDiagnosticSnapshot) return;
        lastDiagnosticSnapshot = snapshot;
        services.Log.Information("[MarketDiagnostic] {Reason}: {Snapshot}", reason, snapshot);
    }

    private void OnItemSearchReceiveEvent(AddonEvent _, AddonArgs args)
    {
        if (pendingPhase != PendingPhase.DiagnosticManualSearch || args is not AddonReceiveEventArgs receive) return;
        services.Log.Information(
            "[MarketCallback] type={EventType}; param={Param}; event=0x{Event:X}; data=0x{Data:X}",
            receive.AtkEventType, receive.EventParam, receive.AtkEvent, receive.AtkEventData);
    }

    private unsafe void SubmitNativeSearch()
    {
        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        if (addon == null || !addon->IsReady || addon->SearchTextInput == null
            || addon->SearchButton == null || addon->ResultsList == null)
            return;

        if (!addon->SearchButton->IsEnabled)
        {
            status = $"Waiting for Search to enable for {pendingItemName}...";
            return;
        }

        addon->RunSearch(true);
        pendingPhase = PendingPhase.WaitingForSearchResults;
        pendingDeadline = DateTime.UtcNow.AddSeconds(12);
        status = $"Searching the marketboard for {pendingItemName}...";
        services.Log.Information("Started native marketboard search for {Item} ({ItemId})", pendingItemName, pendingItemId);
    }

    private unsafe void FinishBatchItemCapture()
    {
        var expectedItemId = pendingItemId;
        var expectedName = pendingItemName;
        if (CaptureAuditListing?.Invoke(expectedItemId) != true)
        {
            CancelPending($"Could not verify native listings for {expectedName}; the scan was stopped.");
            return;
        }

        var results = services.GameGui.GetAddonByName<AtkUnitBase>("ItemSearchResult");
        if (results != null) results->Close(true);
        batchAuditIndex++;
        pendingItemId = 0;
        pendingItemName = "";
        pendingPhase = PendingPhase.None;
        automaticSearch = false;
        nextPendingAction = DateTime.UtcNow.AddMilliseconds(1500);
        BatchAuditStatus = $"Captured {expectedName}: {batchAuditIndex}/{batchAuditItems.Count}.";
    }

    private void StartNextBatchItem()
    {
        if (!IsBatchAuditRunning) return;
        if (batchAuditIndex >= batchAuditItems.Count)
        {
            var total = batchAuditItems.Count;
            batchAuditItems.Clear();
            automaticSearch = false;
            BatchAuditStatus = $"NATIVE SCAN COMPLETE: captured {total}/{total} item(s).";
            return;
        }

        var itemId = batchAuditItems[batchAuditIndex];
        BatchAuditStatus = $"Opening item {batchAuditIndex + 1}/{batchAuditItems.Count}...";
        QueueListing(itemId, true);
    }

    private unsafe void SelectExactSearchResult()
    {
        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentItemSearch*)agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (addon == null || !addon->IsReady || addon->ResultsList == null || agent == null || agent->ItemBuffer == null)
            return;

        var resultCount = Math.Min((int)agent->ItemCount, addon->ResultsList->GetItemCount());
        for (var i = 0; i < resultCount; i++)
        {
            if (agent->ItemBuffer[i] != pendingItemId) continue;
            addon->ResultsList->SelectItem(i, true);
            addon->ResultsList->DispatchItemEvent(i, AtkEventType.ListItemClick);
            pendingPhase = PendingPhase.WaitingForListings;
            pendingDeadline = DateTime.UtcNow.AddSeconds(12);
            status = $"Opening listings for {pendingItemName}...";
            services.Log.Information("Selected native market search row {Row} for {Item} ({ItemId})", i, pendingItemName, pendingItemId);
            return;
        }
    }

    private unsafe bool IsMarketSearchReady()
    {
        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        return addon != null && addon->IsReady
            && addon->SearchTextInput != null && addon->ResultsList != null;
    }

    private IGameObject? FindNearbyMarketBoard()
    {
        var local = services.Objects.LocalPlayer;
        if (local is null) return null;
        return services.Objects
            .Where(x => x is not null && x.IsTargetable
                && (x.BaseId == MarketBoardDataId || x.Name.TextValue.Contains("Market Board", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => Vector3.Distance(local.Position, x.Position))
            .FirstOrDefault(x => Vector3.Distance(local.Position, x.Position) <= 7f);
    }

    private void RefreshStockIfDue()
    {
        if (DateTime.UtcNow < nextStockRefresh) return;
        stock = scanner.ScanItemCounts(BattleMateria.Concat(CraftingMateria).Concat(GatheringMateria)
            .SelectMany(x => new[] { x.Grade12ItemId, x.Grade11ItemId }));
        nextStockRefresh = DateTime.UtcNow.AddMilliseconds(500);
    }

    private static Vector4 StockColor(int count)
        => count == 0 ? new Vector4(1f, .3f, .3f, 1f)
            : count < 25 ? new Vector4(1f, .75f, .25f, 1f)
            : new Vector4(.65f, 1f, .65f, 1f);

    private string ResolveItemName(uint itemId) => services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
        .GetRowOrDefault(itemId)?.Name.ExtractText() ?? "";

    private void CancelPending(string message)
    {
        var wasBatch = IsBatchAuditRunning;
        var total = batchAuditItems.Count;
        pendingItemId = 0;
        pendingItemName = "";
        pendingPhase = PendingPhase.None;
        automaticSearch = false;
        status = message;
        if (wasBatch)
        {
            batchAuditItems.Clear();
            BatchAuditStatus = $"Native scan stopped at {batchAuditIndex}/{total}: {message}";
        }
    }

    public void Dispose()
    {
        services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "ItemSearch", OnItemSearchReceiveEvent);
        services.Framework.Update -= OnFrameworkUpdate;
    }

    private sealed record MarketMateriaRow(string Stat, uint Grade12ItemId, uint Grade11ItemId);
    private enum PendingPhase { None, OpeningBoard, FocusingSearch, DiagnosticManualSearch, WaitingForSearchResults, WaitingForListings, WaitingForCapture }
}
