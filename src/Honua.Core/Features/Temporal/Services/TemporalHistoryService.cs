// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Slice-1 temporal history service for honua-server#1166.
/// </summary>
/// <remarks>
/// <para>
/// Capability discovery rides the Metadata v2 graph and the existing temporal hint
/// (<see cref="FeatureStorageMapping.TemporalColumn"/>). As-of reads are served from the existing
/// change-tracking primitive (<see cref="IChangeTracker"/>) — the closest existing history primitive —
/// rather than a parallel history store.
/// </para>
/// <para>
/// History is considered supported only when the storage mapping declares a temporal column AND the
/// resolved change tracker actually records changes (read-only providers register a no-op tracker, so
/// they report history as unsupported). This interoperates with named-version editing (honua-server#371)
/// without depending on it: a layer can expose temporal history with or without version branches.
/// </para>
/// </remarks>
public sealed partial class TemporalHistoryService : ITemporalHistoryService
{
    private const int DefaultLimit = 1000;
    private const int MaxLimit = 10000;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IChangeTracker _changeTracker;
    private readonly IFeatureReader _featureReader;
    private readonly ITemporalHistoryStore _historyStore;
    private readonly ITemporalCheckpointResolver _checkpointResolver;
    private readonly ITemporalMaskingPolicy _maskingPolicy;
    private readonly ITemporalRollbackRunner _rollbackRunner;

    /// <summary>Creates the temporal history service.</summary>
    public TemporalHistoryService(
        IMetadataV2GraphProvider graphProvider,
        IChangeTracker changeTracker,
        IFeatureReader featureReader,
        ITemporalHistoryStore historyStore,
        ITemporalCheckpointResolver checkpointResolver,
        ITemporalMaskingPolicy maskingPolicy,
        ITemporalRollbackRunner rollbackRunner)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _changeTracker = changeTracker ?? throw new ArgumentNullException(nameof(changeTracker));
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _checkpointResolver = checkpointResolver ?? throw new ArgumentNullException(nameof(checkpointResolver));
        _maskingPolicy = maskingPolicy ?? throw new ArgumentNullException(nameof(maskingPolicy));
        _rollbackRunner = rollbackRunner ?? throw new ArgumentNullException(nameof(rollbackRunner));
    }

    /// <inheritdoc />
    public async Task<TemporalCapability> GetCapabilityAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);

        var temporalColumn = resolved.StorageMapping?.TemporalColumn;
        var historyCapable = !string.IsNullOrWhiteSpace(temporalColumn)
            && resolved.StorageLayerId is not null
            && IsHistoryTrackingTracker(_changeTracker);

        long? currentGeneration = null;
        if (historyCapable)
        {
            currentGeneration = await _changeTracker
                .GetCurrentGenerationAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return new TemporalCapability(
            ServiceId: serviceId,
            LayerId: layerId,
            LayerName: resolved.Resource.Metadata.Name,
            SupportsHistory: historyCapable,
            SupportsAsOf: historyCapable,
            TemporalColumn: temporalColumn,
            CursorKind: TemporalCursorKind.Generation,
            CurrentGeneration: currentGeneration,
            DeferredCapabilities: new TemporalDeferredCapabilities(
                // Slices 2-5 are now implemented; report them as supported when the layer is
                // history-capable so clients negotiate the diff/timeline/attribution/rollback surfaces.
                SupportsDiff: historyCapable,
                SupportsTimeline: historyCapable,
                SupportsAttribution: historyCapable,
                SupportsRollback: historyCapable));
    }

    /// <inheritdoc />
    public async Task<TemporalAsOfResult> ReadAsOfAsync(
        string serviceId,
        int layerId,
        TemporalAsOfRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Generation is not null && request.Timestamp is not null)
        {
            throw new TemporalValidationException(
                "Specify either a generation cursor or a timestamp cursor, not both.");
        }

        // Slice 1 keys exclusively on the change-tracker generation sequence (IChangeTracker exposes
        // GetCurrentGenerationAsync/GetChangesSinceAsync(generation, ...) only — there is no
        // timestamp -> generation index). Rather than silently treating a timestamp as generation 0
        // (which would return the full change feed for any timestamp, including future ones), reject
        // timestamp cursors with a clear 400. Resolving a timestamp to the generation in effect at
        // that point is tracked as a #1166 follow-up.
        if (request.Timestamp is not null)
        {
            throw new TemporalValidationException(
                "Timestamp cursors are not yet supported in this slice; use a generation cursor. "
                + "Timestamp-to-generation resolution is a #1166 follow-up.");
        }

        if (request.Generation is < 0)
        {
            throw new TemporalValidationException("Generation cursor must be non-negative.");
        }

        if (request.Limit is <= 0)
        {
            throw new TemporalValidationException("Limit must be greater than zero when provided.");
        }

        var resolved = await ResolveLayerAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);

        var temporalColumn = resolved.StorageMapping?.TemporalColumn;
        if (string.IsNullOrWhiteSpace(temporalColumn)
            || resolved.StorageLayerId is null
            || !IsHistoryTrackingTracker(_changeTracker))
        {
            throw new TemporalNotSupportedException(
                $"Layer '{resolved.Resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' "
                + "does not support temporal history reads.");
        }

        var storageLayerId = resolved.StorageLayerId.Value;

        // Timestamp cursors are rejected above; slice 1 only resolves generation cursors.
        const TemporalCursorKind requestedKind = TemporalCursorKind.Generation;

        var currentGeneration = await _changeTracker
            .GetCurrentGenerationAsync(cancellationToken)
            .ConfigureAwait(false);

        // The cursor is a generation (changes since this generation, exclusive). A cursor at or beyond
        // the current generation resolves to the current generation, yielding an empty delta.
        var resolvedGeneration = request.Generation ?? 0L;
        if (resolvedGeneration > currentGeneration)
        {
            resolvedGeneration = currentGeneration;
        }

        var changes = await _changeTracker
            .GetChangesSinceAsync(resolvedGeneration, [storageLayerId], cancellationToken)
            .ConfigureAwait(false);

        var limit = request.Limit ?? DefaultLimit;
        limit = Math.Min(limit, MaxLimit);

        var states = ImmutableArray.CreateBuilder<TemporalFeatureState>();
        foreach (var change in changes)
        {
            if (states.Count >= limit)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var kind = (TemporalChangeKind)(short)change.Operation;
            IReadOnlyDictionary<string, object?>? attributes = null;

            // Surviving features (insert/update) expose their current attribute snapshot; deleted
            // features have no retained attribute state in this slice.
            if (kind != TemporalChangeKind.Delete)
            {
                var feature = await _featureReader
                    .GetAsync(storageLayerId, change.ObjectId, cancellationToken)
                    .ConfigureAwait(false);
                if (feature is { } present)
                {
                    attributes = present.Attributes;
                }
            }

            states.Add(new TemporalFeatureState(
                ObjectId: change.ObjectId,
                NetOperation: kind,
                ChangedAtUtc: change.ChangedAt.ToUniversalTime(),
                Attributes: attributes));
        }

        return new TemporalAsOfResult(
            ServiceId: serviceId,
            LayerId: layerId,
            RequestedCursorKind: requestedKind,
            ResolvedGeneration: resolvedGeneration,
            CurrentGeneration: currentGeneration,
            Features: states.ToImmutable());
    }

    private async Task<ResolvedLayer> ResolveLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            throw new TemporalLayerNotFoundException("Service id is required.");
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var service = snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId)
            ? byId
            : snapshot.FindService(serviceId);
        if (service is null)
        {
            throw new TemporalLayerNotFoundException($"Service '{serviceId}' was not found.");
        }

        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, layerId);
        if (publication is null)
        {
            throw new TemporalLayerNotFoundException(
                $"Layer '{layerId}' was not found on service '{serviceId}'.");
        }

        var resource = snapshot.ResolveResource(publication)
            ?? throw new TemporalLayerNotFoundException(
                $"Layer '{layerId}' on service '{serviceId}' does not resolve to a resource.");

        var storageBinding = snapshot.ResolveStorageBinding(publication);
        FeatureStorageMapping? storageMapping = null;
        int? storageLayerId = null;
        if (storageBinding is { StorageType: MetadataV2StorageType.RelationalTable })
        {
            storageMapping = FeatureStorageMapping.FromMetadata(resource, storageBinding);
            storageLayerId = storageBinding.StorageLayerId;
        }

        return new ResolvedLayer(service, publication, resource, storageMapping, storageLayerId);
    }

    // Read-only providers (DuckDB, MySQL/MariaDB) register a no-op change tracker so DI activation
    // succeeds; those layers cannot answer history reads even if a temporal column is configured.
    private static bool IsHistoryTrackingTracker(IChangeTracker tracker)
        => tracker is not Honua.Core.Features.FeatureStore.ReadOnlyProviders.NoOpChangeTracker;

    private sealed record ResolvedLayer(
        MetadataV2Service Service,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        FeatureStorageMapping? StorageMapping,
        int? StorageLayerId);
}
