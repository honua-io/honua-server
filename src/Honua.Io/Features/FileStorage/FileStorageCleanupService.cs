// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal sealed class FileStorageCleanupScheduledTickHandler(FileStorageCleanupService service)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.FileStorageCleanup;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => service.RunCleanupAsync(cancellationToken);
}

internal sealed class FileStorageCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CloudStorageOptions _options;
    private readonly ILogger<FileStorageCleanupService> _logger;

    public FileStorageCleanupService(
        IServiceProvider serviceProvider,
        IOptions<CloudStorageOptions> options,
        ILogger<FileStorageCleanupService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            // Intentionally generic: this is a long-running background loop; a single failed
            // cleanup iteration must not kill the host — log and keep going.
            catch (Exception ex) when (ex is not OutOfMemoryException)
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
