// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// GeoServices-backed <see cref="IReplicaServerStateCapturer"/> that snapshots the server side of each
/// conflicting feature as the conflict-review state envelope
/// <c>{"attributes": {...}, "geometry": ...}</c>, with geometry in GeoServices (Esri) JSON so it is
/// directly comparable to the uploaded client geometry (#1287).
/// </summary>
/// <remarks>
/// The canonical sync service invokes this after conflict detection and before the uploaded edits
/// apply. That is what makes the snapshot trustworthy: the adapter previously captured before the whole
/// upload pipeline ran, so a server edit landing between the capture and detection was flagged as the
/// conflict yet absent from the snapshot, and a later keep-server resolution restored a state that
/// silently discarded it (#2430).
/// </remarks>
internal sealed class FeatureServerReplicaServerStateCapturer : IReplicaServerStateCapturer
{
    private readonly IFeatureReader _featureReader;
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly IReadOnlyDictionary<int, MetadataV2Resource> _resourcesByPublicLayerId;

    public FeatureServerReplicaServerStateCapturer(
        IFeatureReader featureReader,
        IFilterExpressionService filterExpressionService,
        IReadOnlyDictionary<int, MetadataV2Resource> resourcesByPublicLayerId)
    {
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _filterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        _resourcesByPublicLayerId = resourcesByPublicLayerId
            ?? throw new ArgumentNullException(nameof(resourcesByPublicLayerId));
    }

    public async Task<IReadOnlyDictionary<(int PublicLayerId, long ObjectId), ReplicaConflictServerStateCapture>> CaptureAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var states = new Dictionary<(int PublicLayerId, long ObjectId), ReplicaConflictServerStateCapture>();
        if (targets.IsDefaultOrEmpty)
        {
            return states;
        }

        foreach (var target in targets)
        {
            var key = (target.PublicLayerId, target.ObjectId);
            if (states.ContainsKey(key))
            {
                continue;
            }

            var feature = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
            if (feature is { } found)
            {
                states[key] = new ReplicaConflictServerStateCapture(
                    FeatureServerEndpoints.CaptureStateEnvelope(found),
                    FeatureStateToken.Compute(found));
            }
        }

        return states;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, ReplicaConflictStateTokenCapture>> CaptureTokensAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var tokens = new Dictionary<long, ReplicaConflictStateTokenCapture>();
        if (targets.IsDefaultOrEmpty)
        {
            return tokens;
        }

        foreach (var target in targets)
        {
            if (tokens.ContainsKey(target.ObjectId))
            {
                continue;
            }

            var feature = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
            if (feature is { } found)
            {
                tokens[target.ObjectId] = new ReplicaConflictStateTokenCapture(
                    FeatureStateToken.Compute(found),
                    found.Id);
            }
        }

        return tokens;
    }

    private Task<Feature?> ResolveAsync(
        ReplicaConflictCaptureTarget target,
        CancellationToken cancellationToken)
    {
        if (!_resourcesByPublicLayerId.TryGetValue(target.PublicLayerId, out var resource))
        {
            return Task.FromResult<Feature?>(null);
        }

        return GeoServicesFeatureObjectIdResolver.ResolveAsync(
            _featureReader,
            _filterExpressionService,
            resource,
            target.StorageLayerId,
            target.ObjectId,
            cancellationToken);
    }
}
