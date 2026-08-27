using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PentaPenta;

public sealed class Plugin : IDalamudPlugin
{
    private readonly Services services;
    private readonly WindowSystem windows = new("PentaPenta");
    private readonly MainWindow main;
    private readonly MarketBoardOverlay marketBoardOverlay;
    private readonly Melding.MeldController controller;
    private readonly PentameldPricingService pricing;
    private readonly AutoRetainerPricingBridge autoRetainerPricing;
    private readonly RetainerPriceScanCalibration retainerPriceCalibration;
    private readonly RetainerNativePriceSweep retainerNativePriceSweep;
    private readonly MarketBoardReceiveDiagnostic marketBoardDiagnostic;
    private readonly RetainerPricingOverlay retainerPricingOverlay;

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IFramework framework,
        IGameInventory inventory,
        IDataManager data,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IClientState clientState,
        ICondition condition,
        IObjectTable objects,
        IContextMenu contextMenu,
        IMarketBoard marketBoard,
        IPluginLog log)
    {
        services = new Services(pi, commands, framework, inventory, data, gameGui, addonLifecycle, clientState, condition, objects, contextMenu, marketBoard, log);
        var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        if (NormalizeCraftingConfiguration(config)) pi.SavePluginConfig(config);
        var scanner = new InventoryScanner(services);
        controller = new Melding.MeldController(services, config);
        pricing = new PentameldPricingService();
        var retainerListings = new RetainerListingScanner(services);
        var nativeMarketPricing = new NativeMarketPricingScanner(services);
        retainerPriceCalibration = new RetainerPriceScanCalibration(services);
        retainerNativePriceSweep = new RetainerNativePriceSweep(services, nativeMarketPricing);
        marketBoardDiagnostic = new MarketBoardReceiveDiagnostic(services);
        retainerPricingOverlay = new RetainerPricingOverlay(services, config, retainerListings, retainerNativePriceSweep);
        marketBoardOverlay = new MarketBoardOverlay(services, config, scanner);
        autoRetainerPricing = new AutoRetainerPricingBridge(services, config, pricing, retainerListings);
        main = new MainWindow(services, config, scanner, controller, pricing, autoRetainerPricing, retainerListings, nativeMarketPricing, retainerPriceCalibration, retainerNativePriceSweep, marketBoardDiagnostic, marketBoardOverlay, retainerPricingOverlay);
        windows.AddWindow(main);
        windows.AddWindow(marketBoardOverlay);
        windows.AddWindow(retainerPricingOverlay);
        services.Commands.AddHandler("/pentapenta", new CommandInfo((_, _) => main.Toggle()) { HelpMessage = "Open PentaPenta." });
        services.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += main.Toggle;
    }

    private static bool NormalizeCraftingConfiguration(Configuration config)
    {
        var changed = config.Version < 3;
        config.CraftingPresets ??= [];
        config.CraftingMeldTemplates ??= [];
        foreach (var preset in config.CraftingPresets.Values)
            changed |= NormalizeSlots(preset);
        foreach (var template in config.CraftingMeldTemplates)
            changed |= NormalizeSlots(template);
        if (config.Version != 3)
        {
            config.Version = 3;
            changed = true;
        }
        return changed;
    }

    private static bool NormalizeSlots(CraftingMeldPreset preset)
    {
        var normalized = NormalizeSlots(preset.Slots);
        if (preset.Slots is { Count: 5 } && preset.Slots.SequenceEqual(normalized)) return false;
        preset.Slots = normalized;
        return true;
    }

    private static bool NormalizeSlots(CraftingMeldTemplate template)
    {
        var normalized = NormalizeSlots(template.Slots);
        if (template.Slots is { Count: 5 } && template.Slots.SequenceEqual(normalized)) return false;
        template.Slots = normalized;
        return true;
    }

    private static List<CraftingMateria> NormalizeSlots(IReadOnlyCollection<CraftingMateria>? slots)
    {
        var values = slots?.TakeLast(5).ToList() ?? [];
        while (values.Count < 5) values.Add(CraftingMateria.None);
        return values;
    }

    public void Dispose()
    {
        services.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        services.Commands.RemoveHandler("/pentapenta");
        services.PluginInterface.UiBuilder.Draw -= windows.Draw;
        services.PluginInterface.UiBuilder.OpenMainUi -= main.Toggle;
        windows.RemoveAllWindows();
        main.Dispose();
        controller.Dispose();
        autoRetainerPricing.Dispose();
        retainerPriceCalibration.Dispose();
        retainerNativePriceSweep.Dispose();
        marketBoardDiagnostic.Dispose();
        pricing.Dispose();
        marketBoardOverlay.Dispose();
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory target) return;
        if (target.TargetItem is not { } item) return;
        if (item.IsEmpty || !InventoryScanner.IsPlayerBag(item.ContainerType)) return;

        var data = services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(item.BaseItemId);
        if (data is null || data.Value.EquipSlotCategory.RowId == 0 || data.Value.MateriaSlotCount == 0 || !data.Value.IsAdvancedMeldingPermitted)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Pentameld",
            PrefixChar = 'P',
            OnClicked = _ => main.SelectInventoryItem(item),
        });
    }
}
