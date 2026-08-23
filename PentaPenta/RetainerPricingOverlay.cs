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
    private readonly RetainerNativePriceSweep sweep;
    private bool armed;
    private string status = "Arm once, then sweep this retainer's watched 5/5 listings.";

    internal event Action<RetainerListingCapture>? Captured;

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
        this.config = config;
        this.listings = listings;
        this.sweep = sweep;
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
        if (sweep.IsRunning)
        {
            if (ImGui.Button("Stop price scan")) sweep.Stop();
        }
        else
        {
            ImGui.Checkbox("Arm", ref armed);
            ImGui.SameLine();
            if (!armed) ImGui.BeginDisabled();
            if (ImGui.Button("Sweep listings"))
            {
                var capture = listings.Capture(config.PentameldPricingWatchList);
                Captured?.Invoke(capture);
                if (capture.Listings.Count == 0)
                {
                    status = capture.Status;
                }
                else
                {
                    var exclusions = config.PentameldPricingOwnRetainers
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    sweep.Start(capture, config.PentameldPricingWatchList, exclusions, config.PentameldPricingUndercutGil);
                    status = $"Started {capture.RetainerName}: {capture.Listings.Count} listing(s).";
                }
                armed = false;
            }
            if (!armed) ImGui.EndDisabled();
        }
        ImGui.TextWrapped(sweep.Status == "Native retainer scan has not been run." ? status : sweep.Status);
    }
}
