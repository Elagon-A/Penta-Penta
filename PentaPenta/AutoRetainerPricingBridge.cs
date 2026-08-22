using Dalamud.Plugin.Services;

namespace PentaPenta;

internal sealed class AutoRetainerPricingBridge : IDisposable
{
    private const string PluginName = "PentaPenta";
    private const string AdditionalTask = "AutoRetainer.OnRetainerAdditionalTask";
    private const string ReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string RequestPostprocess = "AutoRetainer.RequestPostprocess";
    private const string FinishPostprocess = "AutoRetainer.FinishPostprocessRequest";

    private readonly Services services;
    private readonly Configuration config;
    private readonly PentameldPricingService pricing;
    private readonly Action<string> additionalTaskHandler;
    private readonly Action<string, string> readyHandler;
    private Task<IReadOnlyList<PentameldPriceResult>>? scanTask;
    private bool ownsPostprocessSlot;
    private bool requestPending;

    internal string Status { get; private set; } = "Dry run disabled.";
    internal string LastRetainer { get; private set; } = "";
    internal IReadOnlyList<PentameldPriceResult> LastResults { get; private set; } = [];

    internal AutoRetainerPricingBridge(Services services, Configuration config, PentameldPricingService pricing)
    {
        this.services = services;
        this.config = config;
        this.pricing = pricing;
        additionalTaskHandler = OnAdditionalTask;
        readyHandler = OnReadyForPostprocess;
        services.PluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Subscribe(additionalTaskHandler);
        services.PluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Subscribe(readyHandler);
        services.Framework.Update += OnFrameworkUpdate;
        if (config.EnableAutoRetainerPricingDryRun)
            Status = "Waiting for AutoRetainer to process a retainer.";
    }

    internal void ConfigurationChanged()
    {
        if (!config.EnableAutoRetainerPricingDryRun)
            Status = "Dry run disabled.";
        else if (!ownsPostprocessSlot && scanTask is null)
            Status = "Waiting for AutoRetainer to process a retainer.";
    }

    private void OnAdditionalTask(string retainerName)
    {
        if (!config.EnableAutoRetainerPricingDryRun || config.PentameldPricingWatchList.Count == 0 || requestPending || ownsPostprocessSlot || scanTask is not null)
            return;
        try
        {
            LastRetainer = retainerName;
            Status = $"Requesting a dry-run post-process slot for {retainerName}...";
            requestPending = true;
            services.PluginInterface.GetIpcSubscriber<string, object>(RequestPostprocess).InvokeAction(PluginName);
        }
        catch (Exception ex)
        {
            requestPending = false;
            Status = $"AutoRetainer request failed: {ex.Message}";
        }
    }

    private void OnReadyForPostprocess(string pluginName, string retainerName)
    {
        if (!string.Equals(pluginName, PluginName, StringComparison.Ordinal)) return;
        requestPending = false;
        ownsPostprocessSlot = true;
        LastRetainer = retainerName;
        var worldId = services.Objects.LocalPlayer?.HomeWorld.RowId ?? 0;
        if (worldId == 0)
        {
            Status = "Dry run could not determine the character's home world.";
            Finish();
            return;
        }

        var exclusions = config.PentameldPricingOwnRetainers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(retainerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watch = config.PentameldPricingWatchList
            .Select(x => new PentameldPricingWatchItem { ItemId = x.ItemId, Name = x.Name, Hq = x.Hq })
            .ToList();
        Status = $"Dry-running {watch.Count} watched item(s) for {retainerName}...";
        scanTask = pricing.ScanAsync(worldId, watch, exclusions, config.PentameldPricingUndercutGil);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (scanTask is not { IsCompleted: true } task) return;
        scanTask = null;
        try
        {
            LastResults = task.GetAwaiter().GetResult();
            var proposals = LastResults.Count(x => x.ProposedPrice is not null);
            var errors = LastResults.Count(x => x.Error is not null);
            Status = $"Dry run finished for {LastRetainer}: {proposals} proposal(s)"
                + (errors == 0 ? "." : $", {errors} error(s).");
        }
        catch (Exception ex)
        {
            Status = $"Dry run failed for {LastRetainer}: {ex.Message}";
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        if (!ownsPostprocessSlot) return;
        try
        {
            services.PluginInterface.GetIpcSubscriber<object>(FinishPostprocess).InvokeAction();
        }
        catch (Exception ex)
        {
            Status += $" AutoRetainer release failed: {ex.Message}";
        }
        finally
        {
            ownsPostprocessSlot = false;
            requestPending = false;
        }
    }

    public void Dispose()
    {
        services.Framework.Update -= OnFrameworkUpdate;
        services.PluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Unsubscribe(additionalTaskHandler);
        services.PluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Unsubscribe(readyHandler);
        Finish();
    }
}
