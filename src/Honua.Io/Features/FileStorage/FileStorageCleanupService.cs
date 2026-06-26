// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>
/// Adapts the cloud file-storage expired-file cleanup to the control-plane scheduled-tick dispatcher
/// so EventBridge Scheduler can drive one cleanup under <c>TriggerMode=Event</c> without hosting the
/// in-process timer. Deletes already-expired files via a fresh scope, so a single invocation is safe.
/// <para>On AWS this tick can alternatively be replaced by an S3 lifecycle policy (cheaper, no
/// compute); the tick is retained for on-prem/portability and other storage backends.</para>
/// </summary>
internal sealed class FileStorageCleanupScheduledTickHandler(FileStorageCleanupService service)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.FileStorageCleanup;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => service.RunCleanupAsync(cancellationToken);
}

/// <summary>
/// Background service that periodically cleans up expired temporary files
/// </summary>
internal sealed class FileStorageCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CloudStorageOptions _options;
    private readonly ILogger<FileStorageCleanupService> _logger;

    /// <summary>
    /// Creates a new cleanup service instance
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving scoped services</param>
    /// <param name="options">Cloud storage options</param>
    /// <param name="logger">Logger instance</param>
    public FileStorageCleanupService(
        IServiceProvider serviceProvider,
        IOptions<CloudStorageOptions> options,
        ILogger<FileStorageCleanupService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableAutomaticCleanup)
        {
            FileStorageLog.CleanupDisabled(_logger);
            return;
        }

        FileStorageLog.CleanupServiceStarted(_logger, _options.CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CleanupInterval, stoppingToken);
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown, don't log as error
                break;
            }
            catch (Exception ex)
            {
                FileStorageLog.CleanupError(_logger, ex);
            }
        }

        FileStorageLog.CleanupServiceStopped(_logger);
    }

    internal async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var fileStorage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();

        var cleanedCount = await fileStorage.CleanupExpiredFilesAsync(cancellationToken);

        if (cleanedCount > 0)
        {
            FileStorageLog.CleanupCompleted(_logger, cleanedCount);
        }
    }
}
