using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PentaPenta;

internal sealed class MarketBoardOverlay : Window, IDisposable
{
    private const uint MarketBoardDataId = 2000442;
    private static readonly MarketMateria[] Materia =
    [
        new("Critical Hit", 12, 41772), new("Critical Hit", 11, 41759),
        new("Direct Hit", 12, 41771), new("Direct Hit", 11, 41758),
        new("Determination", 12, 41773), new("Determination", 11, 41760),
        new("Craftsmanship", 12, 41778), new("Craftsmanship", 11, 41765),
        new("Control", 12, 41780), new("Control", 11, 41767),
        new("CP", 12, 41779), new("CP", 11, 41766),
    ];

    private readonly Services services;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private uint pendingItemId;
    private string pendingItemName = "";
    private DateTime pendingDeadline;
    private PendingPhase pendingPhase;
    private DateTime nextStockRefresh;
    private Dictionary<uint, int> stock = [];
    private string status = "Click a materia to open its market listings.";
    private bool wasNearMarketBoard;

    public MarketBoardOverlay(Services services, Configuration config, InventoryScanner scanner)
        : base("PentaPenta Materia Shopping###PentaPentaMarket")
    {
        this.services = services;
        this.config = config;
        this.scanner = scanner;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 360),
            MaximumSize = new Vector2(700, 700),
        };
        IsOpen = false;
        services.Framework.Update += OnFrameworkUpdate;
    }

    public override bool DrawConditions()
        => config.EnableMarketBoardOverlay && services.ClientState.IsLoggedIn && FindNearbyMarketBoard() is not null;

    public override void Draw()
    {
        RefreshStockIfDue();
        ImGui.TextWrapped("Materia shopping list — click an item to open its marketboard listings.");
        ImGui.TextDisabled("The game still requires you to choose and confirm each purchase.");
        ImGui.Separator();

        if (ImGui.BeginTable("market-materia", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Materia");
            ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableHeadersRow();
            foreach (var materia in Materia)
            {
                ImGui.PushID((int)materia.ItemId);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(materia.Stat, false, ImGuiSelectableFlags.SpanAllColumns))
                    QueueListing(materia);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(materia.Grade == 12 ? "XII" : "XI");
                ImGui.TableNextColumn(); DrawStock(stock.GetValueOrDefault(materia.ItemId));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
        ImGui.TextWrapped(status);
    }

    private unsafe void QueueListing(MarketMateria materia)
    {
        pendingItemId = materia.ItemId;
        pendingItemName = services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            .GetRowOrDefault(materia.ItemId)?.Name.ExtractText() ?? "";
        if (pendingItemName.Length == 0)
        {
            CancelPending($"Could not resolve materia item {materia.ItemId}.");
            return;
        }
        pendingDeadline = DateTime.UtcNow.AddSeconds(12);

        if (IsMarketSearchReady())
        {
            RunNativeSearch();
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

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!config.EnableMarketBoardOverlay)
        {
            IsOpen = false;
            wasNearMarketBoard = false;
            if (pendingItemId != 0) CancelPending("Marketboard overlay disabled.");
            return;
        }

        var isNearby = FindNearbyMarketBoard() is not null;
        if (isNearby && !wasNearMarketBoard) IsOpen = true;
        if (!isNearby) IsOpen = false;
        wasNearMarketBoard = isNearby;

        if (pendingItemId == 0) return;
        if (DateTime.UtcNow > pendingDeadline)
        {
            CancelPending("Marketboard opening timed out. Move closer and click the materia again.");
            return;
        }
        if (pendingPhase == PendingPhase.OpeningBoard && IsMarketSearchReady())
        {
            RunNativeSearch();
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
            status = $"Opened listings for {pendingItemName}.";
            pendingItemId = 0;
            pendingItemName = "";
            pendingPhase = PendingPhase.None;
        }
    }

    private unsafe void RunNativeSearch()
    {
        var addon = services.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        if (addon == null || !addon->IsReady || addon->SearchTextInput == null || addon->ResultsList == null)
        {
            CancelPending("The native market search window was not ready. Try the item again.");
            return;
        }

        addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, 0);
        addon->SearchText.SetString(pendingItemName);
        addon->SearchText2.SetString(pendingItemName);
        addon->SearchTextInput->SetText(pendingItemName);
        addon->RunSearch(true);
        pendingPhase = PendingPhase.WaitingForSearchResults;
        pendingDeadline = DateTime.UtcNow.AddSeconds(12);
        status = $"Searching the marketboard for {pendingItemName}...";
        services.Log.Information("Started native marketboard search for {Item} ({ItemId})", pendingItemName, pendingItemId);
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

    private bool IsMarketSearchReady()
        => !services.GameGui.GetAddonByName("ItemSearch").IsNull;

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
        stock = scanner.ScanItemCounts(Materia.Select(x => x.ItemId));
        nextStockRefresh = DateTime.UtcNow.AddMilliseconds(500);
    }

    private static void DrawStock(int count)
    {
        var color = count == 0 ? new Vector4(1f, .3f, .3f, 1f)
            : count < 25 ? new Vector4(1f, .75f, .25f, 1f)
            : new Vector4(.65f, 1f, .65f, 1f);
        ImGui.TextColored(color, count.ToString("N0"));
    }

    private void CancelPending(string message)
    {
        pendingItemId = 0;
        pendingItemName = "";
        pendingPhase = PendingPhase.None;
        status = message;
    }

    public void Dispose() => services.Framework.Update -= OnFrameworkUpdate;

    private sealed record MarketMateria(string Stat, int Grade, uint ItemId);
    private enum PendingPhase { None, OpeningBoard, WaitingForSearchResults, WaitingForListings }
}
