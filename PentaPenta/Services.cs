using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PentaPenta;

internal sealed class Services
{
    [PluginService] internal IDalamudPluginInterface PluginInterface { get; init; } = null!;
    [PluginService] internal ICommandManager Commands { get; init; } = null!;
    [PluginService] internal IFramework Framework { get; init; } = null!;
    [PluginService] internal IGameInventory Inventory { get; init; } = null!;
    [PluginService] internal IDataManager Data { get; init; } = null!;
    [PluginService] internal IGameGui GameGui { get; init; } = null!;
    [PluginService] internal IClientState ClientState { get; init; } = null!;
    [PluginService] internal ICondition Condition { get; init; } = null!;
    [PluginService] internal IPluginLog Log { get; init; } = null!;
}
