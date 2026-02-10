// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Services;

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
        _logger.LogInformation("Temporary file cleanup service started. Cleanup interval: {Interval}", _cleanupInterval);

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during temporary file cleanup");

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

        _logger.LogInformation("Temporary file cleanup service stopped");
    }

    private async Task PerformCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var temporaryFileService = scope.ServiceProvider.GetRequiredService<ITemporaryFileService>();

        _logger.LogDebug("Starting temporary file cleanup");
        await temporaryFileService.CleanupExpiredFilesAsync(cancellationToken);
        _logger.LogDebug("Temporary file cleanup completed");
    }
}
