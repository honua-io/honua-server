// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Plugins;

/// <summary>
/// Hosts every registered <see cref="IPluginBackgroundService"/> as a single
/// <see cref="IHostedService"/> with per-service failure isolation. The whole feature is gated
/// behind the Enterprise <c>plugin.sdk</c> entitlement and the operator kill-switch — when
/// unlicensed/disabled it starts nothing. A background service that throws during execution is
/// logged and dropped (auto-disabled) without affecting the host or sibling services.
/// </summary>
internal sealed partial class PluginBackgroundServiceHost : IHostedService
{
    private readonly IPluginBackgroundService[] _services;
    private readonly ILicenseEntitlementService _licensing;
    private readonly ILogger<PluginBackgroundServiceHost> _logger;
    private readonly bool _enabledByConfig;
    private readonly List<Task> _runners = [];
    private CancellationTokenSource? _stoppingCts;

    public PluginBackgroundServiceHost(
        IEnumerable<IPluginBackgroundService> services,
        ILicenseEntitlementService licensing,
        IOptions<PluginOptions> options,
        ILogger<PluginBackgroundServiceHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _services = [.. services];
        _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabledByConfig = options.Value.Enabled;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_services.Length == 0)
        {
            return Task.CompletedTask;
        }

        if (!_enabledByConfig || !_licensing.CheckEntitlement(FeatureCatalog.PluginSdkKey).IsActive)
        {
            Log.NotStarted(_logger, _services.Length);
            return Task.CompletedTask;
        }

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var service in _services)
        {
            _runners.Add(RunIsolatedAsync(service, _stoppingCts.Token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is null)
        {
            return;
        }

        await _stoppingCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_runners).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline reached; runners observed cancellation or are abandoned.
        }
        finally
        {
            _stoppingCts.Dispose();
            _stoppingCts = null;
        }
    }

    private async Task RunIsolatedAsync(IPluginBackgroundService service, CancellationToken stoppingToken)
    {
        var pluginId = PluginIdOf(service);
        Log.Starting(_logger, pluginId);
        try
        {
            await service.ExecuteAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Failure isolation: a faulting background service is logged and disabled, never
            // propagated to the host (which would tear down the process).
            Log.Faulted(_logger, pluginId, ex);
        }
    }

    private static string PluginIdOf(object instance)
    {
        var type = instance.GetType();
        return type.GetCustomAttribute<PluginAttribute>()?.Id ?? type.FullName ?? type.Name;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9320, Level = LogLevel.Information,
            Message = "Plugin background service '{PluginId}' starting.")]
        public static partial void Starting(ILogger logger, string pluginId);

        [LoggerMessage(EventId = 9321, Level = LogLevel.Error,
            Message = "Plugin background service '{PluginId}' faulted and was disabled.")]
        public static partial void Faulted(ILogger logger, string pluginId, Exception exception);

        [LoggerMessage(EventId = 9322, Level = LogLevel.Warning,
            Message = "{ServiceCount} plugin background service(s) not started: plugin SDK disabled or unlicensed.")]
        public static partial void NotStarted(ILogger logger, int serviceCount);
    }
}
