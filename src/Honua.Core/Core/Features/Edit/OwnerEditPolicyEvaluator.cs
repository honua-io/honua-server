// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

public readonly record struct EditPrincipal(string? Name, bool IsAuthenticated, bool IsAdmin)
{
    public static EditPrincipal Anonymous { get; } = new(null, false, false);
}

public readonly record struct OwnerEditDecision(bool IsAllowed, string? Reason)
{
    public static OwnerEditDecision Allow { get; } = new(true, null);

    public static OwnerEditDecision Deny(string reason) => new(false, reason);
}

public static class OwnerEditPolicyEvaluator
{
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

    public static bool ShouldStampOwnerOnInsert(MetadataV2OwnerEditPolicy? policy)
        => policy is { Enabled: true, StampOwnerOnInsert: true } &&
           !string.IsNullOrEmpty(policy.OwnerField);
}
