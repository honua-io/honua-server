// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Reads durable feature changes used for alert evaluation.
/// </summary>
public interface IAlertChangeReader
{
    /// <summary>
    /// Reads feature changes after a generation cursor.
    /// </summary>
    /// <param name="lastGeneration">Last processed generation (exclusive)</param>
    /// <param name="maxCount">Maximum number of changes to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Ordered changes after the provided generation</returns>
    Task<IReadOnlyList<AlertChange>> GetChangesAfterAsync(
        long lastGeneration,
        int maxCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists and queries alert rules and zone definitions.
/// </summary>
public interface IAlertRuleRepository
{
    /// <summary>
    /// Gets active rules for a service/layer combination.
    /// </summary>
    /// <param name="serviceId">Optional service identifier. When null, service filtering is skipped.</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching active rules</returns>
    Task<IReadOnlyList<AlertRuleDefinition>> GetActiveRulesAsync(
        string? serviceId,
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active rules for multiple service/layer combinations in one batch.
    /// </summary>
    /// <param name="lookupKeys">Distinct service/layer combinations to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching active rules indexed by lookup key.</returns>
    Task<IReadOnlyDictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>> GetActiveRulesAsync(
        IReadOnlyCollection<AlertRuleLookupKey> lookupKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a rule by identifier.
    /// </summary>
    /// <param name="ruleId">Rule identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rule when found; otherwise null</returns>
    Task<AlertRuleDefinition?> GetRuleAsync(
        long ruleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets rules for multiple identifiers in one batch.
    /// </summary>
    /// <param name="ruleIds">Rule identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rules indexed by identifier.</returns>
    Task<IReadOnlyDictionary<long, AlertRuleDefinition>> GetRulesAsync(
        IReadOnlyCollection<long> ruleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets zone definitions for the provided identifiers.
    /// </summary>
    /// <param name="zoneIds">Zone identifiers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Zone definitions indexed by zone id</returns>
    Task<IReadOnlyDictionary<long, AlertZoneDefinition>> GetZonesAsync(
        IReadOnlyCollection<long> zoneIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists evaluator state for rule-feature transitions.
/// </summary>
public interface IAlertStateStore
{
    /// <summary>
    /// Gets state for a single rule-feature pair.
    /// </summary>
    /// <param name="ruleId">Rule identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="objectId">Feature object identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>State snapshot when present; otherwise null</returns>
    Task<AlertStateSnapshot?> GetAsync(
        long ruleId,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets state for multiple rule-feature pairs in one batch.
    /// </summary>
    /// <param name="lookupKeys">Rule-feature lookup keys.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>States indexed by lookup key.</returns>
    Task<IReadOnlyDictionary<AlertStateLookupKey, AlertStateSnapshot>> GetManyAsync(
        IReadOnlyCollection<AlertStateLookupKey> lookupKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates evaluator state for a rule-feature pair.
    /// </summary>
    /// <param name="state">State snapshot to persist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpsertAsync(
        AlertStateSnapshot state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active state rows that are due for dwell evaluation.
    /// </summary>
    /// <param name="dueBefore">Upper bound timestamp for dwell candidates</param>
    /// <param name="maxCount">Maximum number of rows to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>State rows due for dwell processing</returns>
    Task<IReadOnlyList<AlertStateSnapshot>> GetDwellCandidatesAsync(
        DateTimeOffset dueBefore,
        int maxCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores immutable alert events.
/// </summary>
public interface IAlertEventStore
{
    /// <summary>
    /// Appends an event if its dedupe key has not been stored.
    /// </summary>
    /// <param name="alertEvent">Event envelope</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Persisted event id, or null when deduplicated</returns>
    Task<long?> TryAppendAsync(
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an event by identifier.
    /// </summary>
    /// <param name="eventId">Event identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Event when found; otherwise null</returns>
    Task<AlertEventEnvelope?> GetAsync(
        long eventId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores and claims outbox jobs for alert delivery.
/// </summary>
public interface IAlertDispatchStore
{
    /// <summary>
    /// Enqueues dispatch rows for each channel on an event.
    /// </summary>
    /// <param name="eventId">Persisted event identifier</param>
    /// <param name="channels">Channels to enqueue</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnqueueAsync(
        long eventId,
        ImmutableArray<AlertChannelType> channels,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims eligible outbox jobs for processing.
    /// </summary>
    /// <param name="maxCount">Maximum jobs to claim</param>
    /// <param name="now">Current timestamp</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Claimed dispatch jobs</returns>
    Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims eligible digest outbox jobs for batched processing.
    /// </summary>
    /// <param name="maxCount">Maximum jobs to claim</param>
    /// <param name="now">Current timestamp</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Claimed digest dispatch jobs</returns>
    Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingDigestAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a dispatch job as delivered.
    /// </summary>
    /// <param name="dispatchId">Dispatch identifier</param>
    /// <param name="deliveredAt">Delivery timestamp</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkDeliveredAsync(
        long dispatchId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a dispatch job as failed or dead-letter and schedules the next attempt.
    /// </summary>
    /// <param name="dispatchId">Dispatch identifier</param>
    /// <param name="attemptedAt">Attempt timestamp</param>
    /// <param name="nextAttemptAt">Next retry timestamp</param>
    /// <param name="deadLetter">True when retries are exhausted</param>
    /// <param name="errorMessage">Optional failure message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkFailedAsync(
        long dispatchId,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextAttemptAt,
        bool deadLetter,
        string? errorMessage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores and retrieves durable worker checkpoints.
/// </summary>
public interface IAlertCheckpointStore
{
    /// <summary>
    /// Gets checkpoint data for a worker.
    /// </summary>
    /// <param name="workerName">Worker identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Persisted worker checkpoint</returns>
    Task<AlertWorkerCheckpoint> GetAsync(
        string workerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a worker checkpoint.
    /// </summary>
    /// <param name="checkpoint">Checkpoint model</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync(
        AlertWorkerCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}
