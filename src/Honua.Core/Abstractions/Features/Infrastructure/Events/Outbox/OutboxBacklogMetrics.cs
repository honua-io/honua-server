// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

public sealed record OutboxBacklogMetrics
{
    public required long PendingCount { get; init; }

    public required long DeadLetteredCount { get; init; }

    public required double OldestPendingAgeSeconds { get; init; }
}
