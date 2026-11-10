// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// A conflict resolution always commits exactly one feature with rollbackOnFailure true, and the object id is pinned to the conflict record rather than trusted from the envelope, so an operator field cannot redirect the update onto another feature.
internal sealed class FeatureServerReplicaConflictResolutionApplier : IReplicaConflictResolutionApplier
{
    private const string FailureMessage = "The resolved conflict state could not be committed.";

    private readonly FeatureServerEditsHandler _editsHandler;
    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly EditLimits _editLimits;

    public FeatureServerReplicaConflictResolutionApplier(
        FeatureServerEditsHandler editsHandler,
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IFilterExpressionService filterExpressionService,
        IOptions<LimitsOptions> limits)
    {
        _editsHandler = editsHandler ?? throw new ArgumentNullException(nameof(editsHandler));
        _resourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _filterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
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
                    Updates = [PinToConflictFeature(
                        feature,
                        identity,
                        command.ObjectId,
                        command.ReplaceAttributes,
                        command.ExplicitAttributeNames)],
                    RollbackOnFailure = true,
                    RollbackOnFailureExplicitlySet = true,
                };
                break;

            default:
                return new ReplicaConflictApplyResult(Applied: false, FailureMessage);
        }

        // Optimistic-concurrency precondition over the snapshot the caller evaluated its staleness
        // check against, captured through CaptureStateTokenAsync before that check ran. The writer
        // re-computes the token from the locked row, or confirms expected absence, inside the write
        // transaction. Any edit arriving between the check and this write therefore fails the
        // operation instead of being silently overwritten (#2430).
        if (command.ExpectedRowAbsent || command.ExpectedStateToken is { Length: > 0 })
        {
            request.Preconditions = ImmutableArray.Create(new FeatureEditPrecondition
            {
                ObjectId = command.ObjectId,
                ExpectedStateToken = command.ExpectedStateToken,
                ExpectedRowAbsent = command.ExpectedRowAbsent,
            });
        }

        var result = await _editsHandler
            .HandleApplyEditsAsync(command.ServiceId, command.PublicLayerId, request, _editLimits, cancellationToken: cancellationToken)
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

        if (Succeeded(effectResults, command.Effect, command.ExpectedRowAbsent))
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

    public async Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
        string serviceId,
        int publicLayerId,
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveLayerAsync(serviceId, publicLayerId, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return new ReplicaConflictRowSnapshot(Exists: false, StateToken: null, StateJson: null);
        }

        var current = await GeoServicesFeatureObjectIdResolver.ResolveAsync(
                _featureReader,
                _filterExpressionService,
                identity.Value.Resource,
                storageLayerId,
                objectId,
                cancellationToken)
            .ConfigureAwait(false);
        return current is { } row
            ? new ReplicaConflictRowSnapshot(
                Exists: true,
                FeatureStateToken.Compute(row),
                FeatureServerEndpoints.CaptureStateEnvelope(row))
            : new ReplicaConflictRowSnapshot(Exists: false, StateToken: null, StateJson: null);
    }

    private readonly record struct LayerIdentity(
        string ObjectIdField,
        HashSet<string> SchemaFields,
        MetadataV2Resource Resource);

    // The object-id field is resolved within this service, because a layer index such as 0 is reused across services and can pin the update under another service's attribute name.
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
            triple.Resource.SchemaFields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            triple.Resource);
    }

    private static GeoServicesFeature PinToConflictFeature(
        GeoServicesFeature feature,
        LayerIdentity identity,
        long objectId,
        bool replaceAttributes,
        ImmutableArray<string> explicitAttributeNames)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in feature.Attributes.Where(entry => !IsObjectIdKey(entry.Key, identity)))
        {
            attributes[entry.Key] = entry.Value;
        }

        attributes[identity.ObjectIdField] = objectId;

        var explicitlySelected = explicitAttributeNames.IsDefaultOrEmpty
            ? []
            : explicitAttributeNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inheritedNonEditableAttributes = identity.Resource.SchemaFields
            .Where(field =>
                !field.Editable &&
                (!explicitlySelected.Contains(field.Name) || IsObjectIdKey(field.Name, identity)))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = feature.Geometry,
            Centroid = feature.Centroid,
            IncludeGeometry = feature.IncludeGeometry,
            ClearGeometry = feature.IncludeGeometry && feature.Geometry is null,
            ReplaceAttributes = replaceAttributes,
            InheritedNonEditableAttributes = inheritedNonEditableAttributes,
        };
    }

    // A conventional fid or objectid name is an identity alias only when the schema does not declare it, because a custom object-id layer can store that name as a business attribute.
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

    // DeleteNotFound counts as committed only when the caller did not capture an absence guard; accepting it under that guard would let a concurrent reinsert escape.
    private static bool Succeeded(
        EditResult[]? results,
        ReplicaConflictResolutionEffect effect,
        bool requiresTransactionalAbsenceGuard)
        => results is { Length: > 0 }
            && Array.TrueForAll(results, r =>
                r.Success || (!requiresTransactionalAbsenceGuard && IsAlreadyAbsent(r, effect)));

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
