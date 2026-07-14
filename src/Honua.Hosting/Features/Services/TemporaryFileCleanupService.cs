// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Services;

/// <summary>
/// Adapts the temporary-file cleanup to the control-plane scheduled-tick dispatcher so EventBridge
/// Scheduler can drive one cleanup under <c>TriggerMode=Event</c> without hosting the in-process
/// timer. Deletes expired temp files via a fresh scope, so a single invocation is safe.
/// </summary>
internal sealed class TemporaryFileCleanupScheduledTickHandler(TemporaryFileCleanupService service)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.TemporaryFileCleanup;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => service.PerformCleanupAsync(cancellationToken);
}

/// <summary>
/// Background service that periodically cleans up expired temporary files.
/// </summary>
internal sealed class TemporaryFileCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TemporaryFileOptions _options;
    private readonly ILogger<TemporaryFileCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval;

    public TemporaryFileCleanupService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<TemporaryFileOptions> options,
        ILogger<TemporaryFileCleanupService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cleanupInterval = TimeSpan.FromMinutes(30); // Run cleanup every 30 minutes
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TemporaryFileCleanupLog.ServiceStarted(_logger, _cleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                await PerformCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            // Intentional: this is a long-running background cleanup loop; one pass failing
            // must not kill the host — log, back off, and let the while loop retry.
            catch (Exception ex)
            {
                TemporaryFileCleanupLog.CleanupFailed(_logger, ex);

                // Wait a bit before trying again after an error
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        TemporaryFileCleanupLog.ServiceStopped(_logger);
    }

    internal async Task PerformCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var temporaryFileService = scope.ServiceProvider.GetRequiredService<ITemporaryFileService>();

        TemporaryFileCleanupLog.CleanupStarted(_logger);
        await temporaryFileService.CleanupExpiredFilesAsync(cancellationToken);
        TemporaryFileCleanupLog.CleanupCompleted(_logger);
    }
}

internal static partial class TemporaryFileCleanupLog
{
    [LoggerMessage(
        EventId = 8900,
        Level = LogLevel.Information,
        Message = "Temporary file cleanup service started. Cleanup interval: {Interval}")]
    public static partial void ServiceStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(
        EventId = 8901,
        Level = LogLevel.Error,
        Message = "Error occurred during temporary file cleanup")]
    public static partial void CleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 8902,
        Level = LogLevel.Information,
        Message = "Temporary file cleanup service stopped")]
    public static partial void ServiceStopped(ILogger logger);

    [LoggerMessage(
        EventId = 8903,
        Level = LogLevel.Debug,
        Message = "Starting temporary file cleanup")]
    public static partial void CleanupStarted(ILogger logger);

    [LoggerMessage(
        EventId = 8904,
        Level = LogLevel.Debug,
        Message = "Temporary file cleanup completed")]
    public static partial void CleanupCompleted(ILogger logger);
}
