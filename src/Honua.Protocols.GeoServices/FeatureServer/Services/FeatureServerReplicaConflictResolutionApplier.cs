// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
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
/// <remarks>
/// The update target is pinned to <see cref="ReplicaConflictResolutionCommand.ObjectId"/> rather than
/// trusted from the envelope. The envelope's attribute keys come from the storage schema (or from the
/// client's upload), so on a layer whose public object-id field is not literally <c>objectid</c> the
/// shared update handler would fail to find an id, and an operator-supplied <c>fieldValues</c> entry
/// could otherwise redirect one conflict's resolution onto a different feature.
/// </remarks>
internal sealed class FeatureServerReplicaConflictResolutionApplier : IReplicaConflictResolutionApplier
{
    private const string FailureMessage = "The resolved conflict state could not be committed.";

    private readonly FeatureServerEditsHandler _editsHandler;
    private readonly IResourceValidator _resourceValidator;
    private readonly EditLimits _editLimits;

    public FeatureServerReplicaConflictResolutionApplier(
        FeatureServerEditsHandler editsHandler,
        IResourceValidator resourceValidator,
        IOptions<LimitsOptions> limits)
    {
        _editsHandler = editsHandler ?? throw new ArgumentNullException(nameof(editsHandler));
        _resourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
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

                var objectIdField = await ResolveObjectIdFieldNameAsync(command.PublicLayerId, cancellationToken)
                    .ConfigureAwait(false);
                if (objectIdField is null)
                {
                    return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
                }

                request = new ApplyEditsRequest
                {
                    Updates = [PinToConflictFeature(feature, objectIdField, command.ObjectId)],
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

    /// <summary>
    /// Resolves the layer's configured object-id field name, or null when the layer cannot be
    /// resolved (the resolution is then reported as an uncommitted failure rather than dispatched
    /// against a guessed field).
    /// </summary>
    private async Task<string?> ResolveObjectIdFieldNameAsync(int publicLayerId, CancellationToken cancellationToken)
    {
        var layer = await _resourceValidator.ValidateLayerV2Async(publicLayerId, cancellationToken)
            .ConfigureAwait(false);
        return layer is { IsValid: true, Resource: { } resource }
            ? GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource)
            : null;
    }

    /// <summary>
    /// Rewrites the resolved feature so its identity is the conflict's own feature: every
    /// object-id-shaped attribute carried by the captured envelope (or supplied by an operator field
    /// merge) is dropped and replaced with the layer's configured object-id field set to the
    /// conflict's object id. Without this the update would target whatever id the envelope happened to
    /// carry, which the operator can influence through <c>fieldValues</c>.
    /// </summary>
    private static GeoServicesFeature PinToConflictFeature(
        GeoServicesFeature feature,
        string objectIdFieldName,
        long objectId)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in feature.Attributes.Where(entry => !IsObjectIdKey(entry.Key, objectIdFieldName)))
        {
            attributes[entry.Key] = entry.Value;
        }

        attributes[objectIdFieldName] = objectId;

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = feature.Geometry,
            Centroid = feature.Centroid,
            IncludeGeometry = feature.IncludeGeometry,
        };
    }

    /// <summary>
    /// Whether an attribute key names the feature's identity: the layer's configured object-id field,
    /// or one of the conventional aliases a captured storage/client envelope may carry under a
    /// different casing or name than the layer's public field.
    /// </summary>
    private static bool IsObjectIdKey(string key, string objectIdFieldName)
        => key.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase)
            || key.Equals("objectid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("fid", StringComparison.OrdinalIgnoreCase);

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
