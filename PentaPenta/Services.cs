using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PentaPenta;

internal sealed class Services(
    IDalamudPluginInterface pluginInterface,
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
    internal IDalamudPluginInterface PluginInterface { get; } = pluginInterface;
    internal ICommandManager Commands { get; } = commands;
    internal IFramework Framework { get; } = framework;
    internal IGameInventory Inventory { get; } = inventory;
    internal IDataManager Data { get; } = data;
    internal IGameGui GameGui { get; } = gameGui;
    internal IAddonLifecycle AddonLifecycle { get; } = addonLifecycle;
    internal IClientState ClientState { get; } = clientState;
    internal ICondition Condition { get; } = condition;
    internal IObjectTable Objects { get; } = objects;
    internal IContextMenu ContextMenu { get; } = contextMenu;
    internal IPluginLog Log { get; } = log;
}
