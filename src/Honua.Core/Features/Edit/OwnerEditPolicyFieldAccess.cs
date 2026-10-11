// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

/// <summary>
/// One row's owner as read for an edit-policy check. The value is not a response field.
/// </summary>
/// <param name="Visible">True when the row was returned under row security.</param>
/// <param name="Owner">Owner text when <paramref name="Visible"/> is true. Never returned to the caller.</param>
public readonly record struct OwnerFieldSnapshot(bool Visible, object? Owner)
{
    /// <summary>A row the policy read did not return.</summary>
    public static OwnerFieldSnapshot Hidden { get; } = new(false, null);
}

/// <summary>
/// Authorizes an owner-edit policy against the stored owner field without publishing that
/// field to the caller (honua-server#5426). Row visibility still applies. Field masks do not.
/// </summary>
public static class OwnerEditPolicyFieldAccess
{
    /// <summary>
    /// True when an update or delete must read the stored owner. Inserts, anonymous callers,
    /// and administrators do not.
    /// </summary>
    /// <param name="policy">Owner-edit policy, or <c>null</c> when the layer has none.</param>
    /// <param name="principal">Principal performing the edit.</param>
    /// <returns>True when the stored owner is required.</returns>
    public static bool RequiresStoredOwner(MetadataV2OwnerEditPolicy? policy, EditPrincipal principal)
        => policy is { Enabled: true }
            && principal.IsAuthenticated
            && !string.IsNullOrEmpty(principal.Name)
            && !principal.IsAdmin
            && !string.IsNullOrWhiteSpace(policy.OwnerField);

    /// <summary>
    /// True when an update document does not contain the owner field, so the writer must keep
    /// the stored value. The value stays out of the document the caller receives.
    /// </summary>
    /// <param name="policy">Owner-edit policy, or <c>null</c> when the layer has none.</param>
    /// <param name="isPartialUpdate">True for an update that is not a complete attribute replacement.</param>
    /// <param name="attributeNames">Names present on the update document.</param>
    /// <returns>True when omitted masked attributes must be preserved.</returns>
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

    /// <summary>
    /// Decides an edit from a snapshot already loaded for the batch. A null snapshot is used
    /// for inserts and for callers that do not consult the stored owner.
    /// </summary>
    /// <param name="policy">Owner-edit policy, or <c>null</c> when the layer has none.</param>
    /// <param name="editEvent">Edit operation being authorized.</param>
    /// <param name="principal">Principal performing the edit.</param>
    /// <param name="snapshot">Stored owner, or <c>null</c> when no read applies.</param>
    /// <returns>The authorization decision. The reason never includes the owner value.</returns>
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

    /// <summary>
    /// Reads owner values for every visible row in <paramref name="storageFeatureIds"/>.
    /// One query covers the whole batch. The field mask is empty and row security is left
    /// for the provider to apply.
    /// </summary>
    /// <param name="reader">Feature reader that enforces row security.</param>
    /// <param name="layerId">Storage layer id.</param>
    /// <param name="storageFeatureIds">Storage object ids to evaluate.</param>
    /// <param name="ownerField">Attribute name that stores the owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Snapshots keyed by storage object id. A missing key means the row was not visible.</returns>
    public static async Task<IReadOnlyDictionary<long, OwnerFieldSnapshot>> ReadOwnersAsync(
        IFeatureReader reader,
        int layerId,
        IReadOnlyCollection<long> storageFeatureIds,
        string ownerField,
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
            EnforcedMaskedFields = ImmutableArray<string>.Empty
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

    /// <summary>
    /// Decides whether <paramref name="principal"/> may perform <paramref name="editEvent"/>
    /// under <paramref name="policy"/>. Update and delete read the owner through one policy
    /// query. The owner scalar is not returned.
    /// </summary>
    /// <param name="reader">Feature reader that enforces row security.</param>
    /// <param name="layerId">Storage layer id.</param>
    /// <param name="storageFeatureId">
    /// Storage object id of the existing row. Ignored for insert and for decisions that do not
    /// consult the current owner.
    /// </param>
    /// <param name="policy">Owner-edit policy, or <c>null</c> when the layer has none.</param>
    /// <param name="editEvent">Edit operation being authorized.</param>
    /// <param name="principal">Principal performing the edit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authorization decision. The reason never includes the owner value.</returns>
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
            cancellationToken).ConfigureAwait(false);
        var snapshot = owners.TryGetValue(featureId, out var found) ? found : OwnerFieldSnapshot.Hidden;
        return Decide(policy, editEvent, principal, snapshot);
    }
}
