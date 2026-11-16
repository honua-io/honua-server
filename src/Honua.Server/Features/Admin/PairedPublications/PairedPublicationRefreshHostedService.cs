// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed partial class PairedPublicationRefreshHostedService(
    IServiceScopeFactory scopes, ILogger<PairedPublicationRefreshHostedService> logger) : BackgroundService
{
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var refresher = ActivatorUtilities.CreateInstance<PairedPublicationRefresher>(scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<PairedPublicationRefreshLease>());
        await refresher.SweepAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            do
            {
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Log.SweepFailed(logger, exception);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation ends the timer and any sweep during normal host shutdown.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9256, Level = LogLevel.Warning, Message = "Paired publication sweep failed; retrying next tick.")]
        internal static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
