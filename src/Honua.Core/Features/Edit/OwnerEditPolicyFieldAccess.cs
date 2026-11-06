// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

// The owner value is not a response field and is never returned to the caller.
public readonly record struct OwnerFieldSnapshot(bool Visible, object? Owner)
{
    public static OwnerFieldSnapshot Hidden { get; } = new(false, null);
}

// The denial reason never includes the stored owner value.
public readonly record struct OwnerEditBatchPreparation(bool IsAllowed, string? DenialReason, FeatureEditBatch Batch)
{
    public static OwnerEditBatchPreparation Allowed(FeatureEditBatch batch) => new(true, null, batch);

    public static OwnerEditBatchPreparation Denied(string reason) => new(false, reason, default);
}

// The owner-edit check reads the stored owner without publishing that field to the caller.
public static class OwnerEditPolicyFieldAccess
{
    public static bool RequiresStoredOwner(MetadataV2OwnerEditPolicy? policy, EditPrincipal principal)
        => policy is { Enabled: true }
            && principal.IsAuthenticated
            && !string.IsNullOrEmpty(principal.Name)
            && !principal.IsAdmin
            && !string.IsNullOrWhiteSpace(policy.OwnerField);

    // An update that omits the owner field must keep the stored value, and that value stays out of the document the caller receives.
    public static bool ShouldPreserveStoredOwner(
        MetadataV2OwnerEditPolicy? policy,
        bool isPartialUpdate,
        IEnumerable<string> attributeNames)
    {
        ArgumentNullException.ThrowIfNull(attributeNames);
        if (!isPartialUpdate || policy is not { Enabled: true } || string.IsNullOrWhiteSpace(policy.OwnerField))
        {
            return false;
        }

        foreach (var name in attributeNames)
        {
            if (name.Equals(policy.OwnerField, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static OwnerEditDecision Decide(
        MetadataV2OwnerEditPolicy? policy,
        AttributeRuleEditEvent editEvent,
        EditPrincipal principal,
        OwnerFieldSnapshot? snapshot)
    {
        if (editEvent == AttributeRuleEditEvent.Insert || !RequiresStoredOwner(policy, principal))
        {
            return OwnerEditPolicyEvaluator.Evaluate(policy, editEvent, existingOwnerValue: null, principal);
        }

        if (snapshot is not { Visible: true } visible)
        {
            return OwnerEditDecision.Deny("Edit not permitted.");
        }

        return OwnerEditPolicyEvaluator.Evaluate(policy, editEvent, visible.Owner, principal);
    }

    public static async Task<IReadOnlyDictionary<long, OwnerFieldSnapshot>> ReadOwnersAsync(
        IFeatureReader reader,
        int layerId,
        IReadOnlyCollection<long> storageFeatureIds,
        string ownerField,
        VersionContext? versionContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(storageFeatureIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerField);
        if (storageFeatureIds.Count == 0)
        {
            return new Dictionary<long, OwnerFieldSnapshot>();
        }

        var ids = storageFeatureIds.Distinct().ToImmutableArray();
        var requested = ids.ToHashSet();
        var query = new FeatureQuery
        {
            ObjectIds = ids,
            OutFields = ImmutableArray.Create(ownerField),
            Limit = ids.Length,
            // Empty, not null: the resolver stamps a null mask from the caller's field policy,
            // which would hide the owner again. An empty mask suppresses only that concern.
            // EnforcedSqlFilter stays null so row visibility is still resolved.
            EnforcedMaskedFields = ImmutableArray<string>.Empty,
            VersionContext = versionContext is { IsDefault: false } ? versionContext : null
        };

        var page = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var owners = new Dictionary<long, OwnerFieldSnapshot>(ids.Length);
        if (page.Items.IsDefaultOrEmpty)
        {
            return owners;
        }

        foreach (var feature in page.Items)
        {
            if (!requested.Contains(feature.Id))
            {
                continue;
            }

            object? owner = null;
            foreach (var pair in feature.Attributes)
            {
                if (pair.Key.Equals(ownerField, StringComparison.OrdinalIgnoreCase))
                {
                    owner = pair.Value;
                    break;
                }
            }

            owners[feature.Id] = new OwnerFieldSnapshot(true, owner);
        }

        return owners;
    }

    public static async Task<OwnerEditDecision> AuthorizeAsync(
        IFeatureReader reader,
        int layerId,
        long? storageFeatureId,
        MetadataV2OwnerEditPolicy? policy,
        AttributeRuleEditEvent editEvent,
        EditPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (editEvent == AttributeRuleEditEvent.Insert || !RequiresStoredOwner(policy, principal))
        {
            return Decide(policy, editEvent, principal, snapshot: null);
        }

        if (storageFeatureId is not long featureId || string.IsNullOrWhiteSpace(policy!.OwnerField))
        {
            return OwnerEditDecision.Deny("Edit not permitted.");
        }

        var owners = await ReadOwnersAsync(
            reader,
            layerId,
            [featureId],
            policy.OwnerField,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var snapshot = owners.TryGetValue(featureId, out var found) ? found : OwnerFieldSnapshot.Hidden;
        return Decide(policy, editEvent, principal, snapshot);
    }

    public const string OwnerReassignmentDeniedReason = "Only an administrator can change the feature owner.";

    // A full replacement that omits the owner is a reassignment on FeatureServer; other protocols deny only when the owner field is present and differs.
    public static bool RejectsOwnerReassignment(
        bool ownerFieldPresent,
        bool replacesAllAttributes,
        object? submittedOwner,
        object? storedOwner)
    {
        if (!ownerFieldPresent && !replacesAllAttributes)
        {
            return false;
        }

        var submitted = Convert.ToString(submittedOwner, CultureInfo.InvariantCulture);
        var stored = Convert.ToString(storedOwner, CultureInfo.InvariantCulture);
        return !string.Equals(submitted, stored, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<OwnerEditBatchPreparation> PrepareBatchAsync(
        IFeatureReader reader,
        int layerId,
        MetadataV2OwnerEditPolicy? policy,
        FeatureEditBatch batch,
        EditPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (policy is not { Enabled: true })
        {
            return OwnerEditBatchPreparation.Allowed(batch);
        }

        var ids = new List<long>();
        CollectStoredIds(batch, ids);
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners = new Dictionary<long, OwnerFieldSnapshot>();
        if (RequiresStoredOwner(policy, principal) && ids.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(policy.OwnerField))
            {
                return OwnerEditBatchPreparation.Denied("Edit not permitted.");
            }

            owners = await ReadOwnersAsync(
                reader,
                layerId,
                ids,
                policy.OwnerField,
                batch.VersionContext,
                cancellationToken).ConfigureAwait(false);
        }

        var denial = FirstDenial(batch, policy, principal, owners);
        if (denial is not null)
        {
            return OwnerEditBatchPreparation.Denied(denial);
        }

        return OwnerEditBatchPreparation.Allowed(ApplyOwnerFields(batch, policy, principal, owners));
    }

    public static FeatureEditResult DeniedEditResult(FeatureEditBatch batch, string reason)
    {
        var creates = ImmutableArray.CreateBuilder<EditOperationResult>();
        var updates = ImmutableArray.CreateBuilder<EditOperationResult>();
        var deletes = ImmutableArray.CreateBuilder<EditOperationResult>();
        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Create:
                        creates.Add(Failure(reason, operation.Feature?.Id));
                        break;
                    case FeatureEditOperationKind.Update:
                        updates.Add(Failure(reason, operation.Feature?.Id));
                        break;
                    case FeatureEditOperationKind.Delete:
                        deletes.Add(Failure(reason, operation.ObjectId));
                        break;
                }
            }
        }
        else
        {
            if (!batch.Creates.IsDefaultOrEmpty)
            {
                foreach (var feature in batch.Creates)
                {
                    creates.Add(Failure(reason, feature.Id));
                }
            }

            if (!batch.Updates.IsDefaultOrEmpty)
            {
                foreach (var feature in batch.Updates)
                {
                    updates.Add(Failure(reason, feature.Id));
                }
            }

            if (!batch.Deletes.IsDefaultOrEmpty)
            {
                foreach (var id in batch.Deletes)
                {
                    deletes.Add(Failure(reason, id));
                }
            }
        }

        return FeatureEditResult.Rollback(creates.ToImmutable(), updates.ToImmutable(), deletes.ToImmutable());
    }

    private static EditOperationResult Failure(string reason, long? objectId)
        => EditOperationResult.Failure(reason, EditOperationResult.NotPermittedErrorCode, objectId);

    private static void CollectStoredIds(FeatureEditBatch batch, List<long> ids)
    {
        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Update when operation.Feature is { } updated:
                        ids.Add(updated.Id);
                        break;
                    case FeatureEditOperationKind.Delete when operation.ObjectId is { } deleted:
                        ids.Add(deleted);
                        break;
                }
            }
        }

        if (!batch.Updates.IsDefaultOrEmpty)
        {
            foreach (var feature in batch.Updates)
            {
                ids.Add(feature.Id);
            }
        }

        if (!batch.Deletes.IsDefaultOrEmpty)
        {
            foreach (var id in batch.Deletes)
            {
                ids.Add(id);
            }
        }
    }

    private static string? FirstDenial(
        FeatureEditBatch batch,
        MetadataV2OwnerEditPolicy policy,
        EditPrincipal principal,
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners)
    {
        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                var denial = operation.Kind switch
                {
                    FeatureEditOperationKind.Create => AuthorizeInsert(policy, principal),
                    FeatureEditOperationKind.Update => AuthorizeExisting(
                        policy,
                        principal,
                        owners,
                        AttributeRuleEditEvent.Update,
                        operation.Feature?.Id ?? 0,
                        operation.Feature?.Attributes),
                    FeatureEditOperationKind.Delete => AuthorizeExisting(
                        policy,
                        principal,
                        owners,
                        AttributeRuleEditEvent.Delete,
                        operation.ObjectId ?? 0,
                        attributes: null),
                    _ => "Edit not permitted."
                };
                if (denial is not null)
                {
                    return denial;
                }
            }
        }

        if (!batch.Creates.IsDefaultOrEmpty)
        {
            foreach (var _ in batch.Creates)
            {
                var denial = AuthorizeInsert(policy, principal);
                if (denial is not null)
                {
                    return denial;
                }
            }
        }

        if (!batch.Updates.IsDefaultOrEmpty)
        {
            foreach (var feature in batch.Updates)
            {
                var denial = AuthorizeExisting(
                    policy,
                    principal,
                    owners,
                    AttributeRuleEditEvent.Update,
                    feature.Id,
                    feature.Attributes);
                if (denial is not null)
                {
                    return denial;
                }
            }
        }

        if (!batch.Deletes.IsDefaultOrEmpty)
        {
            foreach (var id in batch.Deletes)
            {
                var denial = AuthorizeExisting(
                    policy,
                    principal,
                    owners,
                    AttributeRuleEditEvent.Delete,
                    id,
                    attributes: null);
                if (denial is not null)
                {
                    return denial;
                }
            }
        }

        return null;
    }

    private static string? AuthorizeInsert(MetadataV2OwnerEditPolicy policy, EditPrincipal principal)
    {
        var decision = Decide(policy, AttributeRuleEditEvent.Insert, principal, snapshot: null);
        return decision.IsAllowed ? null : decision.Reason ?? "Edit not permitted.";
    }

    private static string? AuthorizeExisting(
        MetadataV2OwnerEditPolicy policy,
        EditPrincipal principal,
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners,
        AttributeRuleEditEvent editEvent,
        long id,
        ImmutableDictionary<string, object?>? attributes)
    {
        if (RequiresStoredOwner(policy, principal) && id <= 0)
        {
            return "Edit not permitted.";
        }

        OwnerFieldSnapshot? snapshot = null;
        if (RequiresStoredOwner(policy, principal))
        {
            snapshot = owners.TryGetValue(id, out var found) ? found : OwnerFieldSnapshot.Hidden;
        }

        var decision = Decide(policy, editEvent, principal, snapshot);
        if (!decision.IsAllowed)
        {
            return decision.Reason ?? "Edit not permitted.";
        }

        if (editEvent == AttributeRuleEditEvent.Update &&
            !principal.IsAdmin &&
            attributes is not null &&
            RejectsOwnerReassignment(
                ownerFieldPresent: TryGetOwner(attributes, policy.OwnerField, out var submitted),
                replacesAllAttributes: false,
                submittedOwner: submitted,
                storedOwner: snapshot?.Owner))
        {
            return OwnerReassignmentDeniedReason;
        }

        return null;
    }

    private static FeatureEditBatch ApplyOwnerFields(
        FeatureEditBatch batch,
        MetadataV2OwnerEditPolicy policy,
        EditPrincipal principal,
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners)
    {
        var stamp = OwnerEditPolicyEvaluator.ShouldStampOwnerOnInsert(policy);
        var preserve = !principal.IsAdmin && RequiresStoredOwner(policy, principal);
        if (!stamp && !preserve)
        {
            return batch;
        }

        var ownerField = policy.OwnerField;
        var operations = batch.Operations;
        if (!operations.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<FeatureEditOperation>(operations.Length);
            foreach (var operation in operations)
            {
                builder.Add(ApplyOperation(operation, ownerField, principal.Name, owners, stamp, preserve));
            }

            operations = builder.ToImmutable();
        }

        var creates = batch.Creates;
        if (stamp && !creates.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<Feature>(creates.Length);
            foreach (var feature in creates)
            {
                builder.Add(SetOwner(feature, ownerField, principal.Name, replaceExisting: true));
            }

            creates = builder.ToImmutable();
        }

        var updates = batch.Updates;
        if (preserve && !updates.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<Feature>(updates.Length);
            foreach (var feature in updates)
            {
                builder.Add(PreserveOwner(feature, ownerField, owners));
            }

            updates = builder.ToImmutable();
        }

        return batch with
        {
            Operations = operations,
            Creates = creates,
            Updates = updates,
            OwnerGuards = preserve
                ? BuildOwnerGuards(batch, ownerField, owners)
                : batch.OwnerGuards
        };
    }

    private static ImmutableArray<OwnerEditGuard> BuildOwnerGuards(
        FeatureEditBatch batch,
        string ownerField,
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners)
    {
        var ids = new List<long>();
        CollectStoredIds(batch, ids);
        var builder = ImmutableArray.CreateBuilder<OwnerEditGuard>(ids.Count);
        var seen = new HashSet<long>();
        foreach (var id in ids)
        {
            if (!seen.Add(id) || !owners.TryGetValue(id, out var snapshot) || !snapshot.Visible)
            {
                continue;
            }

            builder.Add(new OwnerEditGuard(
                id,
                ownerField,
                Convert.ToString(snapshot.Owner, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return builder.ToImmutable();
    }

    private static FeatureEditOperation ApplyOperation(
        FeatureEditOperation operation,
        string ownerField,
        string? principalName,
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners,
        bool stamp,
        bool preserve)
    {
        if (operation.Feature is not { } feature)
        {
            return operation;
        }

        if (operation.Kind == FeatureEditOperationKind.Create && stamp)
        {
            return operation with { Feature = SetOwner(feature, ownerField, principalName, replaceExisting: true) };
        }

        if (operation.Kind == FeatureEditOperationKind.Update && preserve)
        {
            return operation with { Feature = PreserveOwner(feature, ownerField, owners) };
        }

        return operation;
    }

    private static Feature PreserveOwner(
        Feature feature,
        string ownerField,
        IReadOnlyDictionary<long, OwnerFieldSnapshot> owners)
    {
        if (TryGetOwner(feature.Attributes, ownerField, out _))
        {
            return feature;
        }

        if (!owners.TryGetValue(feature.Id, out var snapshot) || !snapshot.Visible)
        {
            return feature;
        }

        return SetOwner(feature, ownerField, snapshot.Owner, replaceExisting: false);
    }

    private static Feature SetOwner(Feature feature, string ownerField, object? owner, bool replaceExisting)
    {
        var builder = feature.Attributes.ToBuilder();
        string? existingKey = null;
        foreach (var key in builder.Keys)
        {
            if (key.Equals(ownerField, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = key;
                break;
            }
        }

        if (existingKey is null)
        {
            builder.Add(ownerField, owner);
            return feature with { Attributes = builder.ToImmutable() };
        }

        if (!replaceExisting)
        {
            return feature;
        }

        builder[existingKey] = owner;
        return feature with { Attributes = builder.ToImmutable() };
    }

    private static bool TryGetOwner(
        ImmutableDictionary<string, object?> attributes,
        string ownerField,
        out object? owner)
    {
        foreach (var pair in attributes)
        {
            if (pair.Key.Equals(ownerField, StringComparison.OrdinalIgnoreCase))
            {
                owner = pair.Value;
                return true;
            }
        }

        owner = null;
        return false;
    }
}
