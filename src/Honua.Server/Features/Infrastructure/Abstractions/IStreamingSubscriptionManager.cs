// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Abstractions;

internal sealed record StreamingSubscriptionInfo(Guid SubscriberId, DateTimeOffset ConnectedAt, string? ClientLabel);

internal interface IStreamingSubscriptionManager
{
    IReadOnlyList<StreamingSubscriptionInfo> GetSubscriptions();

    bool DisconnectSubscriber(Guid subscriberId);
}
