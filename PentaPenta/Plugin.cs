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
        IPluginLog log)
    {
        services = new Services(pi, commands, framework, inventory, data, gameGui, addonLifecycle, clientState, condition, objects, contextMenu, log);
        var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        var scanner = new InventoryScanner(services);
        controller = new Melding.MeldController(services, config);
        pricing = new PentameldPricingService();
        var retainerListings = new RetainerListingScanner(services);
        var nativeMarketPricing = new NativeMarketPricingScanner(services);
        retainerPriceCalibration = new RetainerPriceScanCalibration(services);
        autoRetainerPricing = new AutoRetainerPricingBridge(services, config, pricing, retainerListings);
        main = new MainWindow(services, config, scanner, controller, pricing, autoRetainerPricing, retainerListings, nativeMarketPricing, retainerPriceCalibration);
        marketBoardOverlay = new MarketBoardOverlay(services, config, scanner);
        windows.AddWindow(main);
        windows.AddWindow(marketBoardOverlay);
        services.Commands.AddHandler("/pentapenta", new CommandInfo((_, _) => main.Toggle()) { HelpMessage = "Open PentaPenta." });
        services.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += main.Toggle;
    }

    public void Dispose()
    {
        services.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        services.Commands.RemoveHandler("/pentapenta");
        services.PluginInterface.UiBuilder.Draw -= windows.Draw;
        services.PluginInterface.UiBuilder.OpenMainUi -= main.Toggle;
        windows.RemoveAllWindows();
        controller.Dispose();
        autoRetainerPricing.Dispose();
        retainerPriceCalibration.Dispose();
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
