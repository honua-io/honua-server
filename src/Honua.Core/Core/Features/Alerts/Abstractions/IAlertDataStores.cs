// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertChangeReader
{
    Task<IReadOnlyList<AlertChange>> GetChangesAfterAsync(
        long lastGeneration,
        int maxCount,
        CancellationToken cancellationToken = default);
}

// A missing image means it is hidden or was not stored, and callers must not substitute the live row.
public interface IAlertCommittedSnapshotSource
{
    Task<Feature?> LoadProjectedSnapshotAsync(
        long generation,
        int layerId,
        CancellationToken cancellationToken = default);
}

public interface IAlertRuleRepository
{
    Task<IReadOnlyList<AlertRuleDefinition>> GetActiveRulesAsync(
        string? serviceId,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>> GetActiveRulesAsync(
        IReadOnlyCollection<AlertRuleLookupKey> lookupKeys,
        CancellationToken cancellationToken = default);

    Task<AlertRuleDefinition?> GetRuleAsync(
        long ruleId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<long, AlertRuleDefinition>> GetRulesAsync(
        IReadOnlyCollection<long> ruleIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<long, AlertZoneDefinition>> GetZonesAsync(
        IReadOnlyCollection<long> zoneIds,
        CancellationToken cancellationToken = default);
}

public interface IAlertStateStore
{
    Task<AlertStateSnapshot?> GetAsync(
        long ruleId,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<AlertStateLookupKey, AlertStateSnapshot>> GetManyAsync(
        IReadOnlyCollection<AlertStateLookupKey> lookupKeys,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        AlertStateSnapshot state,
        CancellationToken cancellationToken = default);

    Task UpsertManyAsync(
        IReadOnlyCollection<AlertStateSnapshot> states,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertStateSnapshot>> GetDwellCandidatesAsync(
        DateTimeOffset dueBefore,
        int maxCount,
        CancellationToken cancellationToken = default);
}

public interface IAlertEventStore
{
    Task<long?> TryAppendAsync(
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default);

    Task<AlertEventEnvelope?> GetAsync(
        long eventId,
        CancellationToken cancellationToken = default);
}

public interface IAlertDispatchStore
{
    Task EnqueueAsync(
        long eventId,
        ImmutableArray<AlertChannelType> channels,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingDigestAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> MarkDeliveredAsync(
        long dispatchId,
        Guid claimToken,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);

    Task<bool> MarkFailedAsync(
        long dispatchId,
        Guid claimToken,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextAttemptAt,
        bool deadLetter,
        string? errorMessage,
        CancellationToken cancellationToken = default);

    // A rate-cap deferral returns the dispatch to pending without counting against the retry budget and never dead-letters it.
    Task<bool> RescheduleAsync(
        long dispatchId,
        Guid claimToken,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task<AlertDispatchBacklog> GetBacklogAsync(
        CancellationToken cancellationToken = default);

    Task<int> PurgeDeliveredAsync(
        DateTimeOffset deliveredBefore,
        int batchLimit,
        CancellationToken cancellationToken = default);

    Task<int> RedriveDeadLettersAsync(
        DateTimeOffset now,
        int batchLimit,
        CancellationToken cancellationToken = default);

    Task SetChannelPausedAsync(
        AlertChannelType channel,
        bool paused,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<AlertChannelType, bool>> GetChannelPauseStatesAsync(
        CancellationToken cancellationToken = default);
}

// The event and its dispatch rows commit in one transaction, so a crash cannot persist the event without its delivery.
public interface IAlertOutboxWriter
{
    Task<ImmutableArray<bool>> CommitEvaluationAsync(
        IReadOnlyCollection<AlertStateSnapshot> states,
        IReadOnlyList<AlertOutboxEntry> dispatches,
        CancellationToken cancellationToken = default);

    Task<long?> AppendAndEnqueueAsync(
        AlertEventEnvelope alertEvent,
        ImmutableArray<AlertChannelType> channels,
        CancellationToken cancellationToken = default);
}

public readonly record struct AlertOutboxEntry(
    AlertEventEnvelope AlertEvent,
    ImmutableArray<AlertChannelType> Channels);

public interface IAlertCheckpointStore
{
    Task<AlertWorkerCheckpoint> GetAsync(
        string workerName,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        AlertWorkerCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}
