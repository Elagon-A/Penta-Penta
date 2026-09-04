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
    private readonly RetainerNativePriceSweep nativeSweep;

    internal RetainerPricingOverlay(
        Services services,
        Configuration config,
        RetainerListingScanner listings,
        MarketBoardOverlay marketBoard,
        RetainerPriceScanCalibration calibration,
        RetainerNativePriceSweep nativeSweep)
        : base("PentaPenta Retainer Pricing###PentaPentaRetainerPricingOverlay",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        this.services = services;
        this.config = config;
        this.listings = listings;
        this.marketBoard = marketBoard;
        this.calibration = calibration;
        this.nativeSweep = nativeSweep;
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

        ImGui.TextDisabled("Retainer-native workflow; does not open the marketboard or materia-shopping overlay.");
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

        ImGui.Separator();
        if (!nativeSweep.IsRowOpenTestArmed)
        {
            if (ImGui.Button("Arm one-row open test")) nativeSweep.ArmRowOpenTest();
        }
        else
        {
            if (ImGui.Button("Open first watched row once")) nativeSweep.RunRowOpenTest(capture);
            ImGui.SameLine();
            if (ImGui.Button("Disarm")) nativeSweep.CancelRowOpenTest();
        }
        ImGui.TextWrapped(nativeSweep.Status);
        ImGui.TextDisabled("Test only: opens Adjust Price, verifies the item name, and never presses Compare Prices.");
    }
}
