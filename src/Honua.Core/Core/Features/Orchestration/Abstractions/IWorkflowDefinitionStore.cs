// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

public interface IWorkflowDefinitionStore
{
    Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default);

    Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default);

    Task<bool> TryClaimScheduleFireAsync(
        string workflowId,
        DateTimeOffset fireTime,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    // Release the fire-time claim only when the winner has not yet advanced the durable cursor, so a peer can retry that occurrence.
    Task ReleaseScheduleClaimAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default);

    // The last-fired cursor survives restart so cron enumeration does not rewind into history.
    Task<DateTimeOffset?> GetScheduleCursorAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    // The cursor moves only forward, so a concurrent replica or clock drift cannot replay a past occurrence after restart.
    Task AdvanceScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default);

    // The pending-run marker stops a crash between run creation and cursor advancement from replaying the occurrence after the claim expires.
    Task SetPendingScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowDefinition>> ListEventTriggeredAsync(CancellationToken cancellationToken = default);

    Task<string?> GetTriggerCursorAsync(
        string workflowId,
        string cursorKind,
        CancellationToken cancellationToken = default);

    // Callers must store a marker that sorts strictly after the previous one, or a replica can fire the same event twice.
    Task SetTriggerCursorAsync(
        string workflowId,
        string cursorKind,
        string marker,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimTriggerFireAsync(
        string workflowId,
        string cursorKind,
        string marker,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    Task ReleaseTriggerClaimAsync(
        string workflowId,
        string cursorKind,
        string marker,
        CancellationToken cancellationToken = default);
}
