using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PentaPenta;

internal sealed class RetainerPricingOverlay : Window
{
    private readonly Services services;
    private readonly Configuration config;
    private readonly RetainerListingScanner listings;
    private readonly MarketBoardOverlay marketBoard;
    private readonly RetainerPriceScanCalibration calibration;

    internal RetainerPricingOverlay(
        Services services,
        Configuration config,
        RetainerListingScanner listings,
        MarketBoardOverlay marketBoard,
        RetainerPriceScanCalibration calibration)
        : base("PentaPenta Retainer Pricing###PentaPentaRetainerPricingOverlay",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        this.services = services;
        this.config = config;
        this.listings = listings;
        this.marketBoard = marketBoard;
        this.calibration = calibration;
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override bool DrawConditions()
        => !services.GameGui.GetAddonByName("RetainerSellList").IsNull
            || marketBoard.IsBatchAuditRunning
            || calibration.IsArmed;

    public override unsafe void PreDraw()
    {
        var addon = services.GameGui.GetAddonByName<AtkUnitBase>("RetainerSellList");
        if (addon == null || addon->RootNode == null) return;
        var width = addon->RootNode->Width * addon->Scale;
        var height = addon->RootNode->Height * addon->Scale;
        var display = ImGui.GetIO().DisplaySize;
        var x = addon->X + width + 6;
        var y = (float)addon->Y;
        if (x + 285 > display.X)
        {
            x = addon->X;
            y = addon->Y + height + 6;
        }
        Position = new Vector2(Math.Max(0, x), Math.Max(0, y));
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("PentaPenta pricing");
        var capture = listings.Capture(config.PentameldPricingWatchList);
        var itemIds = capture.Listings.Select(x => x.ItemId).Distinct().ToList();
        if (!marketBoard.IsBatchAuditRunning || capture.RetainerName.Length > 0)
            ImGui.TextDisabled(capture.RetainerName.Length == 0
                ? capture.Status
                : $"{capture.RetainerName}: {itemIds.Count} watched 5/5 item(s)");

        if (!services.GameGui.GetAddonByName("RetainerSellList").IsNull)
        {
            if (marketBoard.IsBatchAuditRunning || itemIds.Count == 0) ImGui.BeginDisabled();
            if (ImGui.Button($"Scan {itemIds.Count} listings"))
                marketBoard.StartBatchAudit(capture);
            if (marketBoard.IsBatchAuditRunning || itemIds.Count == 0) ImGui.EndDisabled();
        }
        if (marketBoard.IsBatchAuditRunning)
        {
            if (!services.GameGui.GetAddonByName("RetainerSellList").IsNull) ImGui.SameLine();
            if (ImGui.Button("Stop scan")) marketBoard.StopBatchAudit();
        }
        ImGui.TextWrapped(marketBoard.BatchAuditStatus);
        ImGui.TextDisabled("Independent of the materia-shopping overlay. Stops on the first mismatch; stay near a marketboard.");

        ImGui.Separator();
        if (!calibration.IsArmed)
        {
            if (ImGui.Button("Calibrate retainer rows")) calibration.Arm();
        }
        else
        {
            if (ImGui.Button("Stop calibration")) calibration.Cancel();
        }
        ImGui.TextWrapped(calibration.Status);
        ImGui.TextDisabled("Observation only: manually use rows 1 and 3 when prompted. No row event is replayed.");
    }
}
