using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PentaPenta;

public sealed class Plugin : IDalamudPlugin
{
    private readonly Services services;
    private readonly WindowSystem windows = new("PentaPenta");
    private readonly MainWindow main;
    private readonly Melding.MeldController controller;

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IFramework framework,
        IGameInventory inventory,
        IDataManager data,
        IGameGui gameGui,
        IClientState clientState,
        ICondition condition,
        IPluginLog log)
    {
        services = new Services(pi, commands, framework, inventory, data, gameGui, clientState, condition, log);
        var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        var scanner = new InventoryScanner(services);
        controller = new Melding.MeldController(services, config);
        var diagnostics = new Melding.MateriaDiagnostics(services);
        main = new MainWindow(services, config, scanner, controller, diagnostics);
        windows.AddWindow(main);
        services.Commands.AddHandler("/pentapenta", new CommandInfo((_, _) => main.Toggle()) { HelpMessage = "Open PentaPenta." });
        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += main.Toggle;
    }

    public void Dispose()
    {
        services.Commands.RemoveHandler("/pentapenta");
        services.PluginInterface.UiBuilder.Draw -= windows.Draw;
        services.PluginInterface.UiBuilder.OpenMainUi -= main.Toggle;
        windows.RemoveAllWindows();
        controller.Dispose();
    }
}
