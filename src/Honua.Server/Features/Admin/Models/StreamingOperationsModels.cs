// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

internal sealed class SubscriberListResponse
{
    public int SubscriberCount { get; init; }

    public SubscriberInfoResponse[] Subscribers { get; init; } = [];

    public DateTimeOffset GeneratedAt { get; init; }
}

internal sealed class SubscriberInfoResponse
{
    public Guid SubscriberId { get; init; }

    public DateTimeOffset ConnectedAt { get; init; }

    public string? ClientLabel { get; init; }

    public double DurationSeconds { get; init; }
}
