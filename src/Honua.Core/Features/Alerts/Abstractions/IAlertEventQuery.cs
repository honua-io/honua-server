// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertEventQuery
{
    Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default);

    Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default);
}

public interface IAlertLifecycleStore
{
    Task<AlertEventLifecycle?> GetAsync(long eventId, CancellationToken cancellationToken = default);

    // An operator lifecycle change and its audit intent commit in one transaction; the state-only methods are not for operator actions.
    Task<AlertLifecycleTransition> ApplyAsync(
        AlertLifecycleCommand command,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} cannot apply an alert lifecycle mutation and its audit intent atomically. " +
            "An operator lifecycle action must not run against a store that would leave the mutation " +
            "observable without its domain audit record.");

    Task<AlertEventLifecycle?> AcknowledgeAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default);

    Task<AlertEventLifecycle?> SuppressAsync(
        long eventId,
        string actor,
        DateTimeOffset suppressUntil,
        string? note,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken = default);

    Task<AlertEventLifecycle?> ResolveAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);
}
