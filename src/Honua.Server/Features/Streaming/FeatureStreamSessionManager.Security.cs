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
            var authorized = await subscription.Security
                .RefreshUntilRetiredAsync(subscription.Retired, cancellationToken).ConfigureAwait(false);
            return authorized || !entry.IsCurrentSubscription(subscription);
        }
        catch (Exception error) when (error is not OutOfMemoryException && !entry.IsCurrentSubscription(subscription))
        {
            // A removed generation does not participate in the session's authorization.
            return true;
        }
    }
}
