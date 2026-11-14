// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Abstractions;

internal sealed record SubscriptionOptions(string? ClientLabel = null, long? After = null);

internal interface IAlertNotificationSubscription : IDisposable
{
    Guid SubscriberId { get; }

    CancellationToken DisconnectToken { get; }
}
