// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// GeoServices-backed <see cref="IReplicaConflictResolutionApplier"/> that commits an operator-selected
/// disconnected-sync conflict resolution through the shared FeatureServer applyEdits pipeline
/// (<see cref="FeatureServerEditsHandler"/>), so a resolution reuses the same entitlement gate,
/// per-layer data-editor authorization, validation, geometry conversion, telemetry, and transactional
/// outbox behavior as any other edit (#2430).
/// </summary>
/// <remarks>
/// The conflict record stores base/client/server feature states as the opaque envelope
/// <c>{"attributes": {...}, "geometry": ...}</c>, with geometry already in GeoServices (Esri) JSON —
/// the same shape as a wire <see cref="GeoServicesFeature"/>. The applier therefore only has to
/// deserialize the resolved envelope and dispatch a single atomic applyEdits for the conflicting
/// feature; it never touches the feature store directly. <c>rollbackOnFailure</c> is always true: a
/// resolution is a single-feature decision and a partial commit would leave the recorded resolution
/// disagreeing with the committed state.
/// </remarks>
internal sealed class FeatureServerReplicaConflictResolutionApplier : IReplicaConflictResolutionApplier
{
    private const string FailureMessage = "The resolved conflict state could not be committed.";

    private readonly FeatureServerEditsHandler _editsHandler;
    private readonly EditLimits _editLimits;

    public FeatureServerReplicaConflictResolutionApplier(
        FeatureServerEditsHandler editsHandler,
        IOptions<LimitsOptions> limits)
    {
        _editsHandler = editsHandler ?? throw new ArgumentNullException(nameof(editsHandler));
        _editLimits = (limits ?? throw new ArgumentNullException(nameof(limits))).Value.Edits;
    }

    public async Task<ReplicaConflictApplyResult> ApplyAsync(
        ReplicaConflictResolutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ApplyEditsRequest request;
        switch (command.Effect)
        {
            case ReplicaConflictResolutionEffect.None:
                return new ReplicaConflictApplyResult(Applied: true, FailureMessage: null);

            case ReplicaConflictResolutionEffect.DeleteFeature:
                request = new ApplyEditsRequest
                {
                    Deletes = [command.ObjectId],
                    RollbackOnFailure = true,
                    RollbackOnFailureExplicitlySet = true,
                };
                break;

            case ReplicaConflictResolutionEffect.WriteFeatureState:
                if (!TryDeserializeFeature(command.FeatureStateJson, out var feature))
                {
                    return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
                }

                request = new ApplyEditsRequest
                {
                    Updates = [feature],
                    RollbackOnFailure = true,
                    RollbackOnFailureExplicitlySet = true,
                };
                break;

            default:
                return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
        }

        var result = await _editsHandler
            .HandleApplyEditsAsync(command.ServiceId, command.PublicLayerId, request, _editLimits, cancellationToken)
            .ConfigureAwait(false);

        // Anything other than a successful applyEdits payload is an error result from the shared
        // pipeline (entitlement, authorization, validation). Report it as a sanitized failure rather
        // than leaking the pipeline's internal problem detail through the conflict-review surface.
        if (result is not JsonHttpResult<ApplyEditsResponse> { Value: { } response } ||
            !response.Success ||
            HasFailure(response.UpdateResults) ||
            HasFailure(response.DeleteResults))
        {
            return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
        }

        return new ReplicaConflictApplyResult(Applied: true, FailureMessage: null);
    }

    private static bool TryDeserializeFeature(string? featureStateJson, out GeoServicesFeature feature)
    {
        feature = null!;
        if (string.IsNullOrWhiteSpace(featureStateJson))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(featureStateJson, FeatureServerJsonContext.Default.GeoServicesFeature);
            if (parsed is null)
            {
                return false;
            }

            feature = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasFailure(EditResult[]? results)
        => results is null || results.Length == 0 || Array.Exists(results, static r => !r.Success);
}
