using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PentaPenta;

internal sealed class MarketBoardOverlay : Window, IDisposable
{
    private const uint MarketBoardDataId = 2000442;
    private static readonly MarketMateria[] Materia =
    [
        new("Critical Hit", "Savage Aim Materia XII", 41772),
        new("Critical Hit", "Savage Aim Materia XI", 41759),
        new("Direct Hit", "Heavens' Eye Materia XII", 41771),
        new("Direct Hit", "Heavens' Eye Materia XI", 41758),
        new("Determination", "Savage Might Materia XII", 41773),
        new("Determination", "Savage Might Materia XI", 41760),
        new("Craftsmanship", "Competence Materia XII", 41778),
        new("Craftsmanship", "Competence Materia XI", 41765),
        new("Control", "Command Materia XII", 41780),
        new("Control", "Command Materia XI", 41767),
        new("CP", "Cunning Materia XII", 41779),
        new("CP", "Cunning Materia XI", 41766),
    ];

    private readonly Services services;
    private readonly InventoryScanner scanner;
    private uint pendingItemId;
    private string pendingItemName = "";
    private DateTime pendingDeadline;
    private DateTime nextStockRefresh;
    private Dictionary<uint, int> stock = [];
    private string status = "Click a materia to open its market listings.";
    private bool wasNearMarketBoard;

    public MarketBoardOverlay(Services services, InventoryScanner scanner)
        : base("PentaPenta Materia Shopping###PentaPentaMarket")
    {
        this.services = services;
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
        => services.ClientState.IsLoggedIn && FindNearbyMarketBoard() is not null;

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
                ImGui.TableNextColumn(); ImGui.TextUnformatted(materia.Name.EndsWith("XII", StringComparison.Ordinal) ? "XII" : "XI");
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
        pendingItemName = materia.Name;
        pendingDeadline = DateTime.UtcNow.AddSeconds(12);

        if (IsMarketSearchReady())
        {
            OpenListing();
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
        status = $"Opening the marketboard for {pendingItemName}...";
    }

    private void OnFrameworkUpdate(IFramework _)
    {
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
        if (IsMarketSearchReady()) OpenListing();
    }

    private unsafe void OpenListing()
    {
        var agent = (AgentItemSearch*)AgentModule.Instance()->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent == null || agent->InfoProxyItemSearch == null)
        {
            CancelPending("The native market search agent was not ready. Try the item again.");
            return;
        }

        agent->ResultItemId = pendingItemId;
        agent->InfoProxyItemSearch->SearchItemId = pendingItemId;
        agent->InfoProxyItemSearch->RequestData();
        services.Log.Information("Requested marketboard listings for {Item} ({ItemId})", pendingItemName, pendingItemId);
        status = $"Requested listings for {pendingItemName}.";
        pendingItemId = 0;
        pendingItemName = "";
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
        status = message;
    }

    public void Dispose() => services.Framework.Update -= OnFrameworkUpdate;

    private sealed record MarketMateria(string Stat, string Name, uint ItemId);
}
