// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Background service that scans for expired pending manifest approvals and auto-rejects them.
/// </summary>
internal sealed partial class ManifestApprovalExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ManifestApprovalOptions _options;
    private readonly ManifestApprovalWebhookDispatcher? _webhookDispatcher;
    private readonly ILogger<ManifestApprovalExpiryService> _logger;

    public ManifestApprovalExpiryService(
        IServiceScopeFactory scopeFactory,
        IOptions<ManifestApprovalOptions> options,
        ManifestApprovalWebhookDispatcher? webhookDispatcher,
        ILogger<ManifestApprovalExpiryService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _webhookDispatcher = webhookDispatcher;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.ExpiryScanIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_options.Enabled)
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ScanAndExpireAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogExpiryScanFailed(_logger, ex);
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ScanAndExpireAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IManifestPendingChangeStore>();

        var expired = await store.ListExpiredAsync(DateTimeOffset.UtcNow, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var change in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var updated = await store.UpdateDecisionAsync(
                change.PendingId,
                ManifestApprovalStatus.Expired,
                "system",
                "Approval timeout expired.",
                expectedCurrentStatus: ManifestApprovalStatus.Pending,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (updated)
            {
                LogApprovalExpired(_logger, change.PendingId);
                EmitWebhookEvent(change, "manifest-expired");
            }
        }
    }

    private void EmitWebhookEvent(ManifestPendingChange change, string eventType)
    {
        _webhookDispatcher?.Enqueue(new ManifestApprovalWebhookEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            PendingId = change.PendingId,
            ManifestHash = change.ManifestHash,
            Status = "expired",
            Actor = "system",
            Reason = "Approval timeout expired.",
            ResourceCount = change.ResourceCount,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Manifest pending change {PendingId} auto-rejected due to expiry.")]
    private static partial void LogApprovalExpired(ILogger logger, Guid pendingId);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Warning, Message = "Manifest approval expiry scan failed.")]
    private static partial void LogExpiryScanFailed(ILogger logger, Exception exception);
}
