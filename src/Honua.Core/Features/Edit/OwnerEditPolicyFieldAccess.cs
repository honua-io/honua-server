// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Authorizes an owner-edit policy against the stored owner field without publishing that
/// field to the caller (honua-server#5426). Row visibility still applies. Field masks do not.
/// </summary>
public static class OwnerEditPolicyFieldAccess
{
    /// <summary>
    /// Decides whether <paramref name="principal"/> may perform <paramref name="editEvent"/>
    /// under <paramref name="policy"/>. Update and delete read the owner through
    /// <paramref name="reader"/> using a query whose field mask is explicitly empty, so a mask
    /// that hides the owner from ordinary reads does not hide it from this check. The query
    /// leaves <see cref="FeatureQuery.EnforcedSqlFilter"/> unset so the provider still applies
    /// row-level security. The owner scalar is not returned.
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
        ArgumentNullException.ThrowIfNull(reader);

        if (policy is not { Enabled: true }
            || !principal.IsAuthenticated
            || string.IsNullOrEmpty(principal.Name)
            || principal.IsAdmin
            || editEvent == AttributeRuleEditEvent.Insert)
        {
            return OwnerEditPolicyEvaluator.Evaluate(policy, editEvent, existingOwnerValue: null, principal);
        }

        if (storageFeatureId is not long featureId || string.IsNullOrWhiteSpace(policy.OwnerField))
        {
            return OwnerEditDecision.Deny("Edit not permitted.");
        }

        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(featureId),
            OutFields = ImmutableArray.Create(policy.OwnerField),
            Limit = 1,
            // Empty, not null: the resolver stamps a null mask from the caller's field policy,
            // which would hide the owner again. An empty mask suppresses only that concern.
            // EnforcedSqlFilter stays null so row visibility is still resolved.
            EnforcedMaskedFields = ImmutableArray<string>.Empty
        };

        var page = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        object? owner = null;
        var visible = false;
        if (!page.Items.IsDefaultOrEmpty)
        {
            foreach (var feature in page.Items)
            {
                if (feature.Id != featureId)
                {
                    continue;
                }

                visible = true;
                foreach (var pair in feature.Attributes)
                {
                    if (pair.Key.Equals(policy.OwnerField, StringComparison.OrdinalIgnoreCase))
                    {
                        owner = pair.Value;
                        break;
                    }
                }

                break;
            }
        }

        if (!visible)
        {
            return OwnerEditDecision.Deny("Edit not permitted.");
        }

        return OwnerEditPolicyEvaluator.Evaluate(policy, editEvent, owner, principal);
    }
}
