// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

public interface IOutboxHealth
{
    bool IsDispatcherRunning { get; }

    DateTimeOffset? LastDispatchAt { get; }

    OutboxBacklogMetrics? LastBacklog { get; }

    // Each poll kind is tracked on its own so one success does not hide another kind that is still failing.
    bool IsStoragePollFailing { get; }

    DateTimeOffset? LastClaimPollSuccessAt { get; }

    DateTimeOffset? LastClaimPollFailureAt { get; }

    DateTimeOffset? LastRecoveryPollSuccessAt { get; }

    DateTimeOffset? LastRecoveryPollFailureAt { get; }

    DateTimeOffset? LastBacklogPollSuccessAt { get; }

    DateTimeOffset? LastBacklogPollFailureAt { get; }

    DateTimeOffset? LastSuccessfulPollAt { get; }

    DateTimeOffset? LastStorageFailureAt { get; }
}
