// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Collaboration;

// A create is never blocked because a feature that does not exist cannot be leased, and a blocked batch does not call the inner writer.
public sealed class FeatureLockEnforcingFeatureWriter : IFeatureWriter
{
    public const int LockedErrorCode = 423;

    private readonly IFeatureWriter _inner;
    private readonly IFeatureLockService _locks;
    private readonly IFeatureEditGuard _guard;
    private readonly IMetadataV2GraphProvider _metadata;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FeatureLockEnforcingFeatureWriter(
        IFeatureWriter inner,
        IFeatureLockService locks,
        IFeatureEditGuard guard,
        IMetadataV2GraphProvider metadata,
        IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);

        _inner = inner;
        _locks = locks;
        _guard = guard;
        _metadata = metadata;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var transaction = await _inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return new FeatureLockEnforcingTransaction(this, transaction);
    }

    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        // A feature that does not exist yet cannot be leased.
        => _inner.CreateAsync(layerId, feature, cancellationToken);

    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var conflict = await FindConflictAsync(layerId, [feature.Id], "update", cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            throw new FeatureLockedException(FeatureEditLockEnforcement.Describe(conflict));
        }

        return await _inner.UpdateAsync(layerId, feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var conflict = await FindConflictAsync(layerId, [featureId], "delete", cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            throw new FeatureLockedException(FeatureEditLockEnforcement.Describe(conflict));
        }

        return await _inner.DeleteAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
    }

    public Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
        => ApplyEditsAsync(layerId, editBatch, transaction: null, cancellationToken);

    private async Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        IFeatureWriterTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var targets = CollectMutatedObjectIds(editBatch);
        if (targets.Count > 0)
        {
            var conflict = await FindConflictAsync(layerId, targets, "edit", cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                return BuildLockedRollback(editBatch, conflict);
            }
        }

        return transaction is null
            ? await _inner.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false)
            : await transaction.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureEditApplication> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        FeatureEditIdempotencyReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        var targets = CollectMutatedObjectIds(editBatch);
        if (targets.Count > 0)
        {
            var conflict = await FindConflictAsync(layerId, targets, "edit", cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                return new FeatureEditApplication
                {
                    Result = BuildLockedRollback(editBatch, conflict),
                    ReceiptDurable = false,
                };
            }
        }

        return await _inner.ApplyEditsAsync(layerId, editBatch, receipt, cancellationToken).ConfigureAwait(false);
    }

    private sealed class FeatureLockEnforcingTransaction(
        FeatureLockEnforcingFeatureWriter owner,
        IFeatureWriterTransaction inner) : IFeatureWriterTransaction
    {
        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => owner.ApplyEditsAsync(layerId, editBatch, inner, cancellationToken);

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => inner.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static List<long> CollectMutatedObjectIds(FeatureEditBatch editBatch)
    {
        var targets = new List<long>();

        if (!editBatch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in editBatch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Update when operation.Feature is { } feature:
                        targets.Add(feature.Id);
                        break;
                    case FeatureEditOperationKind.Delete when operation.ObjectId is { } objectId:
                        targets.Add(objectId);
                        break;
                    default:
                        break;
                }
            }
        }

        if (!editBatch.Updates.IsDefaultOrEmpty)
        {
            foreach (var update in editBatch.Updates)
            {
                targets.Add(update.Id);
            }
        }

        if (!editBatch.Deletes.IsDefaultOrEmpty)
        {
            targets.AddRange(editBatch.Deletes);
        }

        return targets;
    }

    private async Task<FeatureEditConflictResponse?> FindConflictAsync(
        int layerId,
        List<long> objectIds,
        string operation,
        CancellationToken cancellationToken)
    {
        if (objectIds.Count == 0 ||
            !await FeatureEditLockEnforcement.IsEvaluationRequiredAsync(_locks, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var serviceNames = await ResolveLeaseServiceNamesAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (serviceNames.Count == 0)
        {
            return null;
        }

        var holder = FeatureEditLockEnforcement.ResolveHolder(_httpContextAccessor.HttpContext);
        foreach (var objectId in objectIds)
        {
            foreach (var serviceName in serviceNames)
            {
                var conflict = await FeatureEditLockEnforcement.EvaluateAsync(
                    _guard, serviceName, layerId, objectId, operation, holder, cancellationToken)
                    .ConfigureAwait(false);
                if (conflict is not null)
                {
                    return conflict;
                }
            }
        }

        return null;
    }

    // One storage layer may be published under several service names, and a lease under any of them protects the row.
    private async Task<IReadOnlyList<string>> ResolveLeaseServiceNamesAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        MetadataV2GraphSnapshot snapshot;
        try
        {
            snapshot = await _metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No usable graph (a host wired for storage-only access). Nothing can have been
            // claimed against a service name, so there is nothing to enforce.
            return [];
        }

        var servicesById = snapshot.Graph.Services.ToDictionary(
            static service => service.Metadata.Id,
            static service => service.Metadata.Name,
            StringComparer.Ordinal);

        var names = new List<string>();
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (snapshot.ResolveStorageLayerId(publication, resource: null) != layerId)
            {
                continue;
            }

            if (servicesById.TryGetValue(publication.ServiceId, out var name) &&
                !string.IsNullOrWhiteSpace(name) &&
                !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static FeatureEditResult BuildLockedRollback(
        FeatureEditBatch editBatch,
        FeatureEditConflictResponse conflict)
    {
        var message = FeatureEditLockEnforcement.Describe(conflict);

        static ImmutableArray<EditOperationResult> Failures(int count, string message)
            => count == 0
                ? ImmutableArray<EditOperationResult>.Empty
                : Enumerable
                    .Range(0, count)
                    .Select(_ => EditOperationResult.Failure(message, LockedErrorCode))
                    .ToImmutableArray();

        var operationCreates = 0;
        var operationUpdates = 0;
        var operationDeletes = 0;
        if (!editBatch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in editBatch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Create:
                        operationCreates++;
                        break;
                    case FeatureEditOperationKind.Update:
                        operationUpdates++;
                        break;
                    case FeatureEditOperationKind.Delete:
                        operationDeletes++;
                        break;
                    default:
                        break;
                }
            }
        }

        var creates = Math.Max(operationCreates, editBatch.Creates.IsDefaultOrEmpty ? 0 : editBatch.Creates.Length);
        var updates = Math.Max(operationUpdates, editBatch.Updates.IsDefaultOrEmpty ? 0 : editBatch.Updates.Length);
        var deletes = Math.Max(operationDeletes, editBatch.Deletes.IsDefaultOrEmpty ? 0 : editBatch.Deletes.Length);

        return FeatureEditResult.Rollback(
            Failures(creates, message),
            Failures(updates, message),
            Failures(deletes, message));
    }
}

// Single-feature update and delete throw because those entry points have no per-operation error slot.
public sealed class FeatureLockedException : InvalidOperationException
{
    public FeatureLockedException(string message)
        : base(message)
    {
    }

    public FeatureLockedException()
        : base("The feature is locked for editing by another editor.")
    {
    }

    public FeatureLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
