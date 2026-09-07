// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Collaboration;

/// <summary>
/// Decorates the provider <see cref="IFeatureWriter"/> so a collaborative-editing lease is
/// binding at the shared edit-pipeline boundary, not only on the protocol handlers that
/// remembered to ask (#4402).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator and not only handler checks.</b> Every protocol adapter that mutates
/// features funnels through <see cref="IFeatureWriter.ApplyEditsAsync(int, FeatureEditBatch, CancellationToken)"/>
/// — GeoServices <c>applyEdits</c>, OGC API Features, OData single-entity writes <em>and</em>
/// its atomic change-set path, WFS-T, and the gRPC feature service. Enforcing only in the
/// handlers would leave an authorized editor able to overwrite a leased feature simply by
/// choosing a different supported write surface. The handler checks remain because they
/// produce each protocol's natural refusal shape; this decorator is the guarantee that no
/// path — including one added later — can bypass a lease.
/// </para>
/// <para>
/// <b>How a blocked batch is reported.</b> As a rolled-back <see cref="FeatureEditResult"/>
/// whose operations carry error code <c>423</c>. That is the pre-existing lock-aware-writer
/// contract: <c>GeoServicesEditErrorCodes.FromHttpStatus</c> already maps 423 to the
/// published per-edit <c>FeatureLocked</c> code, so an Esri client sees code 1005 with no
/// protocol change, and every other adapter already handles a rolled-back writer result.
/// Nothing is written — the inner writer is never called.
/// </para>
/// <para>
/// <b>Creates are never blocked</b>: a feature that does not exist yet cannot be leased.
/// </para>
/// <para>
/// <b>Cost.</b> Gated on a single <see cref="IFeatureLockService.HasAnyActiveLeasesAsync"/>
/// probe, so a deployment with no lease outstanding — the overwhelmingly common case, and
/// the only case at all until an operator supplies an <see cref="IFeatureLockAuthorizer"/> —
/// pays one dictionary emptiness check per batch and nothing else.
/// </para>
/// </remarks>
public sealed class FeatureLockEnforcingFeatureWriter : IFeatureWriter
{
    /// <summary>
    /// HTTP status carried on a blocked operation. 423 Locked is the status the GeoServices
    /// per-edit <c>FeatureLocked</c> code is defined to map from.
    /// </summary>
    public const int LockedErrorCode = 423;

    private readonly IFeatureWriter _inner;
    private readonly IFeatureLockService _locks;
    private readonly IFeatureEditGuard _guard;
    private readonly IMetadataV2GraphProvider _metadata;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureLockEnforcingFeatureWriter"/> class.
    /// </summary>
    /// <param name="inner">The provider writer being decorated.</param>
    /// <param name="locks">The lease store.</param>
    /// <param name="guard">The concurrency guard that renders the verdict.</param>
    /// <param name="metadata">Metadata graph provider, used to map a storage layer id back to the lease namespace.</param>
    /// <param name="httpContextAccessor">Accessor for the request being served, if any.</param>
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

    /// <inheritdoc />
    public Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
        => _inner.BeginTransactionAsync(isolationLevel, cancellationToken);

    /// <inheritdoc />
    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        // A feature that does not exist yet cannot be leased.
        => _inner.CreateAsync(layerId, feature, cancellationToken);

    /// <inheritdoc />
    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var conflict = await FindConflictAsync(layerId, [feature.Id], "update", cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            throw new FeatureLockedException(FeatureEditLockEnforcement.Describe(conflict));
        }

        return await _inner.UpdateAsync(layerId, feature, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var conflict = await FindConflictAsync(layerId, [featureId], "delete", cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            throw new FeatureLockedException(FeatureEditLockEnforcement.Describe(conflict));
        }

        return await _inner.DeleteAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
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

        return await _inner.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects every object id the batch would mutate. Creates are excluded.
    /// </summary>
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

    /// <summary>
    /// Returns the first lease conflict among the targeted features, or <see langword="null"/>.
    /// </summary>
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

    /// <summary>
    /// Maps a storage layer id back to the service names a client could have claimed a lease
    /// under.
    /// </summary>
    /// <remarks>
    /// The lease namespace is client-facing — the routed service name and published layer id
    /// — while the writer only knows the storage handle. One layer is commonly published
    /// through several services (a FeatureServer layer, an OGC collection, a MapServer
    /// layer), so every candidate name is checked: a lease claimed under any of them
    /// protects the row.
    /// </remarks>
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
            if ((publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication)) != layerId)
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

    /// <summary>
    /// Builds the rolled-back result for a batch blocked by a lease: nothing was written and
    /// every operation reports the lock.
    /// </summary>
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

/// <summary>
/// Thrown by the single-feature writer entry points when a collaborative-editing lease held
/// by another editor blocks the mutation.
/// </summary>
/// <remarks>
/// The batch entry point reports the refusal as a rolled-back result instead, because that
/// is the shape every protocol adapter already handles. The single-feature
/// <c>UpdateAsync</c>/<c>DeleteAsync</c> entry points return the feature (or a bool) with no
/// slot for a per-operation error, so an exception is the only faithful signal available.
/// </remarks>
public sealed class FeatureLockedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureLockedException"/> class.
    /// </summary>
    /// <param name="message">A description of the blocking lease.</param>
    public FeatureLockedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureLockedException"/> class.
    /// </summary>
    public FeatureLockedException()
        : base("The feature is locked for editing by another editor.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureLockedException"/> class.
    /// </summary>
    /// <param name="message">A description of the blocking lease.</param>
    /// <param name="innerException">The underlying cause.</param>
    public FeatureLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
