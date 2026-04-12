// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Background service that periodically runs workspace cleanup sweeps.
/// </summary>
internal sealed class WorkspaceCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly WorkspaceOptions _options;
    private readonly ILogger<WorkspaceCleanupService> _logger;

    public WorkspaceCleanupService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<WorkspaceOptions> options,
        ILogger<WorkspaceCleanupService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableAutomaticCleanup)
        {
            WorkspaceCleanupLog.CleanupDisabled(_logger);
            return;
        }

        WorkspaceCleanupLog.ServiceStarted(_logger, _options.CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CleanupInterval, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WorkspaceCleanupLog.SweepFailed(_logger, ex);
            }
        }

        WorkspaceCleanupLog.ServiceStopped(_logger);
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var lifecycleService = scope.ServiceProvider.GetRequiredService<IWorkspaceLifecycleService>();

        WorkspaceCleanupLog.SweepStarted(_logger);
        var result = await lifecycleService.RunCleanupAsync(cancellationToken);

        if (result.WorkspacesExpired > 0 || result.WorkspacesDeleted > 0)
        {
            WorkspaceCleanupLog.SweepCompleted(_logger,
                result.WorkspacesExpired, result.WorkspacesDeleted,
                result.ArtifactsDeleted, result.BytesReclaimed);
        }

        foreach (var error in result.Errors)
        {
            WorkspaceCleanupLog.SweepPartialError(_logger, error);
        }
    }
}

internal static partial class WorkspaceCleanupLog
{
    [LoggerMessage(
        EventId = 9910,
        Level = LogLevel.Information,
        Message = "Workspace cleanup service started. Interval: {Interval}")]
    public static partial void ServiceStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(
        EventId = 9911,
        Level = LogLevel.Information,
        Message = "Workspace cleanup service stopped")]
    public static partial void ServiceStopped(ILogger logger);

    [LoggerMessage(
        EventId = 9912,
        Level = LogLevel.Information,
        Message = "Workspace automatic cleanup is disabled")]
    public static partial void CleanupDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 9913,
        Level = LogLevel.Debug,
        Message = "Starting workspace cleanup sweep")]
    public static partial void SweepStarted(ILogger logger);

    [LoggerMessage(
        EventId = 9914,
        Level = LogLevel.Information,
        Message = "Workspace cleanup: expired={WorkspacesExpired}, deleted={WorkspacesDeleted}, artifacts={ArtifactsDeleted}, bytes={BytesReclaimed}")]
    public static partial void SweepCompleted(ILogger logger, int workspacesExpired, int workspacesDeleted, int artifactsDeleted, long bytesReclaimed);

    [LoggerMessage(
        EventId = 9915,
        Level = LogLevel.Error,
        Message = "Error during workspace cleanup sweep")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 9916,
        Level = LogLevel.Warning,
        Message = "Partial error during cleanup: {Error}")]
    public static partial void SweepPartialError(ILogger logger, string error);
}
