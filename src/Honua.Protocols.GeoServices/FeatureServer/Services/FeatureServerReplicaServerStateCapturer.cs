// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;

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

    public FeatureServerReplicaServerStateCapturer(IFeatureReader featureReader)
    {
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
    }

    public async Task<IReadOnlyDictionary<(int PublicLayerId, long ObjectId), string>> CaptureAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var states = new Dictionary<(int PublicLayerId, long ObjectId), string>();
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

            var feature = await _featureReader
                .GetAsync(target.StorageLayerId, target.ObjectId, cancellationToken)
                .ConfigureAwait(false);
            if (feature is { } found)
            {
                states[key] = FeatureServerEndpoints.CaptureStateEnvelope(found);
            }
        }

        return states;
    }
}
