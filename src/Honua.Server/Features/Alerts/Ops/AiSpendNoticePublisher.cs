// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Alerts.Ops;

internal static class AiSpendNoticePublisher
{
    internal sealed record Batch<T>(IReadOnlyList<T> Pending,
        Func<T, CancellationToken, Task<bool>> MarkQueued, Func<T, OpsNotification> CreateNotification);

    internal static async Task<int> PublishAsync<T>(
        OpsNotificationService notifications, Batch<T> batch, CancellationToken cancellationToken)
    {
        var queued = 0;
        foreach (var notice in batch.Pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await notifications.TryNotifyAsync(batch.CreateNotification(notice), cancellationToken).ConfigureAwait(false)
                && await batch.MarkQueued(notice, cancellationToken).ConfigureAwait(false))
            {
                queued++;
            }
        }
        return queued;
    }

    internal static string DedupeIdentifier(string identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
}
