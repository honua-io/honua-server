// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal sealed record AlertDispatchObservation(AlertDispatchBacklog Backlog, DateTimeOffset? ObservedAt);

internal interface IAlertDispatchHealth
{
    bool IsDispatcherRunning { get; }

    bool IsDispatcherEnabled { get; }

    DateTimeOffset? LastPollAt { get; }

    DateTimeOffset? BacklogObservedAt { get; }

    AlertDispatchObservation? LastObservation { get; }

    AlertDispatchBacklog? LastBacklog { get; }

    bool IsStoragePollFailing { get; }

    // Post-action verification refreshes so it cannot declare convergence from a stale sample.
    Task<AlertDispatchBacklog> RefreshBacklogAsync(CancellationToken cancellationToken = default);
}
