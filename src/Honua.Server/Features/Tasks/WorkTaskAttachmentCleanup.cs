// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Tasks;

internal sealed class WorkTaskAttachmentCleanup(IWorkTaskAttachmentStore attachments, ICloudFileStorage storage)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var expired = await attachments.ScheduleCleanupAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var attachment in expired)
        {
            if (await storage.DeleteAsync(attachment.BlobKey, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }
        return removed;
    }
}

internal sealed partial class WorkTaskAttachmentCleanupWorker(IServiceScopeFactory scopes, ILogger<WorkTaskAttachmentCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IWorkTaskAttachmentStore>() is null)
                {
                    return;
                }
                await scope.ServiceProvider.GetRequiredService<WorkTaskAttachmentCleanup>().RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { CleanupFailed(logger, exception); }
        }
    }

    [LoggerMessage(EventId = 118460, Level = LogLevel.Warning, Message = "Task attachment cleanup failed; expired uploads remain scheduled for retry.")]
    private static partial void CleanupFailed(ILogger logger, Exception exception);
}
