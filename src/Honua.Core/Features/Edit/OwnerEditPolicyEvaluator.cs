// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

/// <summary>
/// The authenticated principal an edit is being performed on behalf of, resolved by the
/// protocol adapter from the request's auth context and passed into the shared edit pipeline
/// so ownership-based access control is enforced consistently rather than by caller discipline.
/// </summary>
/// <param name="Name">
/// The principal's stable name/identity used as the owner value. <c>null</c> or empty for an
/// anonymous (unauthenticated) caller.
/// </param>
/// <param name="IsAuthenticated">Whether the caller presented an authenticated identity.</param>
/// <param name="IsAdmin">
/// Whether the caller holds an administrative/override role that bypasses the ownership check.
/// </param>
public readonly record struct EditPrincipal(string? Name, bool IsAuthenticated, bool IsAdmin)
{
    /// <summary>An anonymous, unauthenticated, non-admin principal.</summary>
    public static EditPrincipal Anonymous { get; } = new(null, false, false);
}

/// <summary>
/// Outcome of an owner-based edit-policy check for a single feature operation.
/// </summary>
/// <param name="IsAllowed">True when the operation is authorized under the policy.</param>
/// <param name="Reason">
/// Operator-facing denial reason when <see cref="IsAllowed"/> is false; <c>null</c> when allowed.
/// </param>
public readonly record struct OwnerEditDecision(bool IsAllowed, string? Reason)
{
    /// <summary>A reusable allowed decision.</summary>
    public static OwnerEditDecision Allow { get; } = new(true, null);

    /// <summary>Creates a denial decision carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">The denial reason.</param>
    /// <returns>A denial decision.</returns>
    public static OwnerEditDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Shared, provider-agnostic evaluator for owner-based edit policies (ownership-based access
/// control, #2132). When a resource declares an enabled <see cref="MetadataV2OwnerEditPolicy"/>,
/// update/delete of a feature is authorized only when the requesting principal owns the row
/// (the owner field equals the principal's name); administrators bypass the check, inserts stamp
/// the owner from the principal, and anonymous edits are rejected while the policy is active. A
/// disabled or absent policy preserves full-edit behavior.
/// </summary>
public static class OwnerEditPolicyEvaluator
{
    /// <summary>
    /// Evaluates whether <paramref name="principal"/> may perform <paramref name="editEvent"/>
    /// on a feature whose current owner-field value is <paramref name="existingOwnerValue"/>.
    /// </summary>
    /// <param name="policy">The resource's owner-edit policy, or <c>null</c>/disabled for full edit.</param>
    /// <param name="editEvent">The edit operation being authorized.</param>
    /// <param name="existingOwnerValue">
    /// The owner-field value of the existing row for an update/delete. Ignored for inserts.
    /// </param>
    /// <param name="principal">The principal performing the edit.</param>
    /// <returns>The authorization decision.</returns>
    public static OwnerEditDecision Evaluate(
        MetadataV2OwnerEditPolicy? policy,
        AttributeRuleEditEvent editEvent,
        object? existingOwnerValue,
        EditPrincipal principal)
    {
        if (policy is not { Enabled: true })
        {
            return OwnerEditDecision.Allow;
        }

        // Anonymous edits are rejected whenever an owner-based policy is active.
        if (!principal.IsAuthenticated || string.IsNullOrEmpty(principal.Name))
        {
            return OwnerEditDecision.Deny("Anonymous edits are not permitted while an owner-based edit policy is active.");
        }

        // Administrators (override role) bypass the ownership check for every operation.
        if (principal.IsAdmin)
        {
            return OwnerEditDecision.Allow;
        }

        // Inserts are owned by their creator; the owner is stamped, not checked.
        if (editEvent == AttributeRuleEditEvent.Insert)
        {
            return OwnerEditDecision.Allow;
        }

        var existingOwner = Convert.ToString(existingOwnerValue, CultureInfo.InvariantCulture);
        if (string.Equals(existingOwner, principal.Name, StringComparison.OrdinalIgnoreCase))
        {
            return OwnerEditDecision.Allow;
        }

        return OwnerEditDecision.Deny("Feature is owned by another principal and cannot be modified under the owner-based edit policy.");
    }

    /// <summary>
    /// Whether an insert under <paramref name="policy"/> should stamp the owner field with the
    /// principal's name.
    /// </summary>
    /// <param name="policy">The resource's owner-edit policy.</param>
    /// <returns>True when the owner field should be stamped on insert.</returns>
    public static bool ShouldStampOwnerOnInsert(MetadataV2OwnerEditPolicy? policy)
        => policy is { Enabled: true, StampOwnerOnInsert: true } &&
           !string.IsNullOrEmpty(policy.OwnerField);
}
