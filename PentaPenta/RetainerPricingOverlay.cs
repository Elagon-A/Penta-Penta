using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PentaPenta;

internal sealed class RetainerPricingOverlay : Window
{
    private readonly Services services;

    internal RetainerPricingOverlay(
        Services services,
        Configuration config,
        RetainerListingScanner listings,
        RetainerNativePriceSweep sweep)
        : base("PentaPenta Retainer Pricing###PentaPentaRetainerPricingOverlay",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        this.services = services;
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override bool DrawConditions()
        => !services.GameGui.GetAddonByName("RetainerSellList").IsNull;

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
        ImGui.TextWrapped("Automatic row sweep disabled for safety. Use Guided open-retainer price audit in PentaPenta.");
    }
}
