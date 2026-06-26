// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Durable store for declarative workflow definitions.
/// </summary>
public interface IWorkflowDefinitionStore
{
    /// <summary>
    /// Retrieves a workflow definition by identifier.
    /// </summary>
    Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to atomically create a workflow definition.
    /// </summary>
    /// <returns>True when created; false when a definition with the same identifier already exists.</returns>
    Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the workflow definition with the given identifier.
    /// </summary>
    Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all stored workflow definitions.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns workflow definitions whose trigger is cron-based and enabled.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the workflow definition with the given identifier.
    /// </summary>
    /// <returns>True when a definition was removed.</returns>
    Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a single scheduler fire-time occurrence for the given workflow so
    /// only one replica creates the corresponding run. The claim is valid for the supplied
    /// retention window and is idempotent across restarts.
    /// </summary>
    /// <returns>True when this caller successfully claimed the occurrence; false when another replica has already claimed it.</returns>
    Task<bool> TryClaimScheduleFireAsync(
        string workflowId,
        DateTimeOffset fireTime,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a previously-won schedule fire-time claim so another tick (on this replica
    /// or a peer) can retry the same occurrence. Intended for use when the winner encounters
    /// a transient failure creating the corresponding run and has not yet advanced the
    /// durable schedule cursor past the occurrence.
    /// </summary>
    Task ReleaseScheduleClaimAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the durable "last fired" cursor for the workflow's scheduler. Returned value
    /// survives restarts so cron enumeration never rewinds into previously-fired history.
    /// </summary>
    /// <returns>The last fired occurrence time, or <c>null</c> when the workflow has never fired.</returns>
    Task<DateTimeOffset?> GetScheduleCursorAsync(
        string workflowId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the durable "last fired" cursor for the workflow's scheduler. Implementations
    /// must only move the cursor forward so concurrent replicas and clock drift cannot cause
    /// historical occurrences to be replayed after restart.
    /// </summary>
    Task AdvanceScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably records that a run has been created for the given fire time but the schedule
    /// cursor has not yet advanced past it. <see cref="GetScheduleCursorAsync"/> incorporates
    /// this marker so a process crash between run creation and cursor advancement cannot
    /// cause the occurrence to be replayed after the per-firing claim TTL expires.
    /// </summary>
    Task SetPendingScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns event-triggered workflow definitions whose trigger is enabled and is of a kind
    /// the scheduler evaluates per tick (change-feed, object-store). Cron definitions are
    /// returned by <see cref="ListScheduledAsync"/> instead.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListEventTriggeredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the durable string-valued trigger cursor for the workflow under the given
    /// <paramref name="cursorKind"/> (e.g. the last-fired change-feed generation or the last
    /// object-store marker). Returns <c>null</c> when the workflow has never fired for that kind.
    /// </summary>
    Task<string?> GetTriggerCursorAsync(
        string workflowId,
        string cursorKind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the durable string-valued trigger cursor for the workflow. Unlike the cron cursor
    /// (which is a monotonic timestamp), the event-trigger cursor stores an opaque comparable
    /// marker so generation numbers and object-store markers share one mechanism. Callers must
    /// only set a marker that sorts strictly after the previous one to preserve at-most-once
    /// semantics across replicas.
    /// </summary>
    Task SetTriggerCursorAsync(
        string workflowId,
        string cursorKind,
        string marker,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a single event-trigger firing identified by an opaque marker so only
    /// one replica creates the corresponding run. Reuses the same distributed fire-claim
    /// semantics as <see cref="TryClaimScheduleFireAsync"/>.
    /// </summary>
    /// <returns>True when this caller claimed the firing; false when another replica already has.</returns>
    Task<bool> TryClaimTriggerFireAsync(
        string workflowId,
        string cursorKind,
        string marker,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a previously-won event-trigger firing claim so another tick can retry it after a
    /// transient run-creation failure.
    /// </summary>
    Task ReleaseTriggerClaimAsync(
        string workflowId,
        string cursorKind,
        string marker,
        CancellationToken cancellationToken = default);
}
