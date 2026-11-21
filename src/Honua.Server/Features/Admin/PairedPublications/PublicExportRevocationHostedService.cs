// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.PairedPublications;

// Resumes any non-terminal public export revocation records on startup and retries them on a steady cadence.
internal sealed partial class PublicExportRevocationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PublicExportRevocationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                if (scope.ServiceProvider.GetService<IPublicPublicationDestination>() is null)
                {
                    return;
                }

                var revocation = scope.ServiceProvider.GetService<PublicExportRevocationService>();
                if (revocation is not null)
                {
                    await revocation.ResumePendingAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogRevocationSweepFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 10341, Level = LogLevel.Warning,
        Message = "Public export revocation recovery sweep failed; pending revocations will retry on the next tick.")]
    private static partial void LogRevocationSweepFailed(ILogger logger, Exception exception);
}
