// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
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
    private readonly IFeatureReader _featureReader;
    private readonly EditLimits _editLimits;

    public FeatureServerReplicaConflictResolutionApplier(
        FeatureServerEditsHandler editsHandler,
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IOptions<LimitsOptions> limits)
    {
        _editsHandler = editsHandler ?? throw new ArgumentNullException(nameof(editsHandler));
        _resourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
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

                var layer = await ResolveLayerAsync(command.ServiceId, command.PublicLayerId, cancellationToken)
                    .ConfigureAwait(false);
                if (layer is not { } identity)
                {
                    return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
                }

                request = new ApplyEditsRequest
                {
                    Updates = [PinToConflictFeature(feature, identity, command.ObjectId)],
                    RollbackOnFailure = true,
                    RollbackOnFailureExplicitlySet = true,
                };
                break;

            default:
                return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
        }

        // The caller observed no row. A delete then has nothing to remove and MUST NOT be dispatched:
        // the object id could have been reinserted between that observation and now, and the shared
        // pipeline has no way to express "only if still absent", so issuing the delete would remove
        // that new row. Re-read instead: still absent means the target state already holds, present
        // means a reinsert the resolution must not act on (#2430).
        if (command.ExpectedRowAbsent && command.Effect == ReplicaConflictResolutionEffect.DeleteFeature)
        {
            var reinserted = command.StorageLayerId is { } absentLayerId
                && await _featureReader.GetAsync(absentLayerId, command.ObjectId, cancellationToken)
                    .ConfigureAwait(false) is not null;
            return reinserted
                ? new ReplicaConflictApplyResult(Applied: false, FailureMessage, PreconditionFailed: true)
                : new ReplicaConflictApplyResult(Applied: true, FailureMessage: null);
        }

        // Optimistic-concurrency precondition over the snapshot the caller evaluated its staleness
        // check against, captured through CaptureStateTokenAsync before that check ran. The writer
        // re-computes the token from the locked row inside the write transaction, so any edit arriving
        // between the check and this write fails the operation instead of being silently overwritten
        // (#2430). Absent when the conflict has no storage-layer id or the row was already gone.
        if (command.ExpectedStateToken is { Length: > 0 } expectedStateToken)
        {
            request.Preconditions = ImmutableArray.Create(new FeatureEditPrecondition
            {
                ObjectId = command.ObjectId,
                ExpectedStateToken = expectedStateToken,
            });
        }

        var result = await _editsHandler
            .HandleApplyEditsAsync(command.ServiceId, command.PublicLayerId, request, _editLimits, cancellationToken)
            .ConfigureAwait(false);

        // Anything other than a successful applyEdits payload is an error result from the shared
        // pipeline (entitlement, authorization, validation). Report it as a sanitized failure rather
        // than leaking the pipeline's internal problem detail through the conflict-review surface.
        if (result is not JsonHttpResult<ApplyEditsResponse> { Value: { } response })
        {
            return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
        }

        // Only the result collection matching the dispatched effect is meaningful: an update request
        // returns no delete results and vice versa, so inspecting both would report every successful
        // resolution as failed after the edit had already committed.
        var effectResults = command.Effect == ReplicaConflictResolutionEffect.DeleteFeature
            ? response.DeleteResults
            : response.UpdateResults;

        if (Succeeded(effectResults, command.Effect))
        {
            return new ReplicaConflictApplyResult(Applied: true, FailureMessage: null);
        }

        // An indeterminate row is NOT a failure: the writer is saying the resolution may have
        // committed. Reporting it as uncommitted releases the claim, and if the write did land the
        // next attempt sees this resolution's own change as a post-conflict edit and returns Stale
        // forever (#2430).
        return new ReplicaConflictApplyResult(
            Applied: false,
            FailureMessage,
            CommitOutcomeUnknown: CommitOutcomeUnknown(effectResults),
            PreconditionFailed: PreconditionFailed(effectResults));
    }

    /// <inheritdoc />
    public async Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        var current = await _featureReader
            .GetAsync(storageLayerId, objectId, cancellationToken)
            .ConfigureAwait(false);
        return current is { } row
            ? new ReplicaConflictRowSnapshot(
                Exists: true,
                FeatureStateToken.Compute(row),
                FeatureServerEndpoints.CaptureStateEnvelope(row))
            : new ReplicaConflictRowSnapshot(Exists: false, StateToken: null, StateJson: null);
    }

    /// <summary>
    /// The layer's configured object-id field plus the names it actually declares, used to decide which
    /// attributes carry identity and which are business data.
    /// </summary>
    private readonly record struct LayerIdentity(string ObjectIdField, HashSet<string> SchemaFields);

    /// <summary>
    /// Resolves the object-id field of the layer as published by this service, together with its
    /// declared field names, or null when the pair cannot be resolved (the resolution is then reported
    /// as an uncommitted failure rather than dispatched against a guessed field). The lookup is
    /// service-scoped: service-local layer indexes such as <c>0</c> are reused across services, so
    /// resolving the index alone can return another service's resource and pin the update under the
    /// wrong attribute name.
    /// </summary>
    private async Task<LayerIdentity?> ResolveLayerAsync(
        string serviceId,
        int publicLayerId,
        CancellationToken cancellationToken)
    {
        var layer = await _resourceValidator
            .ValidateServiceLayerV2Async(serviceId, publicLayerId, cancellationToken)
            .ConfigureAwait(false);
        if (layer is not { IsValid: true, Resource: { } triple })
        {
            return null;
        }

        return new LayerIdentity(
            GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(triple.Resource),
            triple.Resource.SchemaFields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
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
        LayerIdentity identity,
        long objectId)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in feature.Attributes.Where(entry => !IsObjectIdKey(entry.Key, identity)))
        {
            attributes[entry.Key] = entry.Value;
        }

        attributes[identity.ObjectIdField] = objectId;

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
    /// different name than the layer's public field.
    /// </summary>
    /// <remarks>
    /// An alias only counts as identity when the layer does not actually declare a field by that name.
    /// A layer whose object-id field is a custom key can legitimately also have a business attribute
    /// called <c>fid</c> or <c>objectid</c>; dropping it would silently discard the operator's value,
    /// so a keep-server restoration or field merge targeting it would report success while leaving the
    /// field unchanged (#2430).
    /// </remarks>
    private static bool IsObjectIdKey(string key, LayerIdentity identity)
        => key.Equals(identity.ObjectIdField, StringComparison.OrdinalIgnoreCase)
            || ((key.Equals("objectid", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("fid", StringComparison.OrdinalIgnoreCase))
                && !identity.SchemaFields.Contains(key));

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

    /// <summary>
    /// Whether the result collection for the dispatched effect reports the single expected row as
    /// committed. A resolution always dispatches exactly one edit, so an absent or empty collection
    /// means the row was never touched and is a failure — but only the collection belonging to the
    /// dispatched effect is consulted.
    /// </summary>
    /// <remarks>
    /// A delete that reports <see cref="GeoServicesEditErrorCodes.DeleteNotFound"/> counts as
    /// committed. That is what makes the resolution write genuinely idempotent, which the recovery path
    /// depends on: an interrupted attempt whose delete already landed re-dispatches it, and treating
    /// "already absent" as a failure would release the claim and strand the conflict forever (#2430).
    /// The desired end state — the feature is gone — holds either way.
    /// </remarks>
    private static bool Succeeded(EditResult[]? results, ReplicaConflictResolutionEffect effect)
        => results is { Length: > 0 }
            && Array.TrueForAll(results, r => r.Success || IsAlreadyAbsent(r, effect));

    private static bool PreconditionFailed(EditResult[]? results)
        => results is { Length: > 0 }
            && Array.Exists(results, r => r.Error?.Code == GeoServicesEditErrorCodes.UpdateConflict);

    private static bool CommitOutcomeUnknown(EditResult[]? results)
        => results is { Length: > 0 }
            && Array.Exists(results, r => r.Error?.Code == GeoServicesEditErrorCodes.CommitOutcomeUnknown);

    private static bool IsAlreadyAbsent(EditResult result, ReplicaConflictResolutionEffect effect)
        => effect == ReplicaConflictResolutionEffect.DeleteFeature
            && result.Error?.Code == GeoServicesEditErrorCodes.DeleteNotFound;
}
