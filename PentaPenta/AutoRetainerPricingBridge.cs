using Dalamud.Plugin.Services;

namespace PentaPenta;

internal sealed class AutoRetainerPricingBridge : IDisposable
{
    private const string PluginName = "PentaPenta";
    private const string AdditionalTask = "AutoRetainer.OnRetainerAdditionalTask";
    private const string ReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string RequestPostprocess = "AutoRetainer.RequestPostprocess";
    private const string FinishPostprocess = "AutoRetainer.FinishPostprocessRequest";
    private const string ListTaskButtonsDraw = "AutoRetainer.OnRetainerListTaskButtonsDraw";
    private const string ListCustomTask = "AutoRetainer.OnRetainerListCustomTask";

    private readonly Services services;
    private readonly Configuration config;
    private readonly PentameldPricingService pricing;
    private readonly RetainerListingScanner retainerListings;
    private readonly Action<string> additionalTaskHandler;
    private readonly Action<string, string> readyHandler;
    private Task<IReadOnlyList<PentameldPriceResult>>? scanTask;
    private bool ownsPostprocessSlot;
    private bool requestPending;
    private readonly Action listTaskButtonsHandler;

    internal string Status { get; private set; } = "Idle.";
    internal string LastRetainer { get; private set; } = "";
    internal IReadOnlyList<PentameldPriceResult> LastResults { get; private set; } = [];
    internal bool IsBusy => requestPending || ownsPostprocessSlot || scanTask is not null;
    internal bool AutomaticListingAuditActive { get; private set; }
    internal event Action? AutomaticListingAuditStarted;
    internal event Action<RetainerListingCapture>? AutomaticListingAuditCaptured;

    internal AutoRetainerPricingBridge(Services services, Configuration config, PentameldPricingService pricing, RetainerListingScanner retainerListings)
    {
        this.services = services;
        this.config = config;
        this.pricing = pricing;
        this.retainerListings = retainerListings;
        additionalTaskHandler = OnAdditionalTask;
        readyHandler = OnReadyForPostprocess;
        listTaskButtonsHandler = DrawAutoRetainerAuditButton;
        services.PluginInterface.GetIpcSubscriber<string, object>(AdditionalTask).Subscribe(additionalTaskHandler);
        services.PluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocess).Subscribe(readyHandler);
        services.PluginInterface.GetIpcSubscriber<object>(ListTaskButtonsDraw).Subscribe(listTaskButtonsHandler);
        services.Framework.Update += OnFrameworkUpdate;
    }

    internal void CompleteAutomaticListingAudit()
    {
        AutomaticListingAuditActive = false;
    }

    private void DrawAutoRetainerAuditButton()
    {
        var disabled = AutomaticListingAuditActive || IsBusy;
        if (disabled) Dalamud.Bindings.ImGui.ImGui.BeginDisabled();
        if (Dalamud.Bindings.ImGui.ImGui.Button("Audit pentameld listings###PentaPentaAutoAudit"))
        {
            AutomaticListingAuditActive = true;
            AutomaticListingAuditStarted?.Invoke();
            try
            {
                services.PluginInterface.GetIpcSubscriber<string, object>(ListCustomTask).InvokeAction(PluginName);
                Status = "AutoRetainer listing audit requested.";
            }
            catch (Exception ex)
            {
                AutomaticListingAuditActive = false;
                Status = $"Could not start AutoRetainer listing audit: {ex.Message}";
            }
        }
        if (disabled) Dalamud.Bindings.ImGui.ImGui.EndDisabled();
    }

    internal void RunManualDryTest()
    {
        if (IsBusy)
        {
            Status = "A pricing dry run is already active.";
            return;
        }
        if (config.PentameldPricingWatchList.Count == 0)
        {
            Status = "Add at least one item to the pricing watchlist first.";
            return;
        }
        StartScan("Manual test", null);
    }

    private void OnAdditionalTask(string retainerName)
    {
        if (!AutomaticListingAuditActive || config.PentameldPricingWatchList.Count == 0 || requestPending || ownsPostprocessSlot || scanTask is not null)
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
        if (AutomaticListingAuditActive)
        {
            var capture = retainerListings.CaptureLoadedActiveRetainer(config.PentameldPricingWatchList, retainerName);
            AutomaticListingAuditCaptured?.Invoke(capture);
        }
        Finish();
    }

    private void StartScan(string displayName, string? activeRetainer)
    {
        LastRetainer = displayName;
        var worldId = services.Objects.LocalPlayer?.HomeWorld.RowId ?? 0;
        if (worldId == 0)
        {
            Status = "Dry run could not determine the character's home world.";
            Finish();
            return;
        }

        var exclusions = config.PentameldPricingOwnRetainers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeRetainer)) exclusions.Add(activeRetainer);
        var watch = config.PentameldPricingWatchList
            .Select(x => new PentameldPricingWatchItem { ItemId = x.ItemId, Name = x.Name, Hq = x.Hq })
            .ToList();
        Status = $"Dry-running {watch.Count} watched item(s) for {displayName}...";
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
        services.PluginInterface.GetIpcSubscriber<object>(ListTaskButtonsDraw).Unsubscribe(listTaskButtonsHandler);
        Finish();
    }
}
