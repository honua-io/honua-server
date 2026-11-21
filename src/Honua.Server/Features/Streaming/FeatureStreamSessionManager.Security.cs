// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

internal sealed partial class FeatureStreamSessionManager
{
    // False when a current subscription of the session lost access. Subscriptions removed or
    // replaced while their refresh runs do not decide the session's authorization.
    internal async Task<bool> RefreshSessionSecurityAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            return true;
        }

        foreach (var subscription in entry.GetSubscriberSecurities())
        {
            if (!await RefreshCurrentSubscriptionAsync(entry, subscription, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> RefreshCurrentSubscriptionAsync(
        SessionEntry entry, SessionEntry.SubscriberSecuritySnapshot subscription, CancellationToken cancellationToken)
    {
        try
        {
            if (subscription.Retired.IsCompleted)
            {
                return true;
            }

            using var refreshStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var refresh = subscription.Security.RefreshAsync(refreshStop.Token);
            var completed = await Task.WhenAny(refresh, subscription.Retired).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (completed == subscription.Retired)
            {
                await refreshStop.CancelAsync().ConfigureAwait(false);
                // A store may ignore cancellation. Observe its eventual failure without
                // making a retired generation consume the remaining session deadline.
                _ = refresh.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return true;
            }

            var outcome = await refresh.ConfigureAwait(false);
            return outcome != StreamSubscriberSecurityRefreshOutcome.Revoked || !entry.IsCurrentSubscription(subscription);
        }
        catch (Exception error) when (error is not OutOfMemoryException && !entry.IsCurrentSubscription(subscription))
        {
            // A removed generation does not participate in the session's authorization.
            return true;
        }
    }
}
