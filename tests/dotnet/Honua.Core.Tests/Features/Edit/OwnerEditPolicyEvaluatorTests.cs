// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

/// <summary>
/// Unit coverage for <see cref="OwnerEditPolicyEvaluator"/> (#2132): ownership-based access
/// control on the shared edit path. Verifies an owner-matched update is allowed, an
/// owner-mismatched update is denied, an administrator bypasses the check, anonymous edits are
/// denied while the policy is active, and a disabled/absent policy preserves full-edit behavior.
/// </summary>
public sealed class OwnerEditPolicyEvaluatorTests
{
    private static readonly MetadataV2OwnerEditPolicy EnabledPolicy = new()
    {
        Enabled = true,
        OwnerField = "owner"
    };

    [UnitTest]
    public void Evaluate_OwnerMatchesUpdate_IsAllowed()
    {
        var decision = OwnerEditPolicyEvaluator.Evaluate(
            EnabledPolicy,
            AttributeRuleEditEvent.Update,
            existingOwnerValue: "alice",
            principal: new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false));

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_OwnerMismatchUpdate_IsDenied()
    {
        var decision = OwnerEditPolicyEvaluator.Evaluate(
            EnabledPolicy,
            AttributeRuleEditEvent.Delete,
            existingOwnerValue: "bob",
            principal: new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false));

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    public void Evaluate_AdminPrincipal_BypassesOwnershipCheck()
    {
        var decision = OwnerEditPolicyEvaluator.Evaluate(
            EnabledPolicy,
            AttributeRuleEditEvent.Update,
            existingOwnerValue: "bob",
            principal: new EditPrincipal("admin", IsAuthenticated: true, IsAdmin: true));

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_AnonymousPrincipal_IsDeniedWhenPolicyActive()
    {
        var decision = OwnerEditPolicyEvaluator.Evaluate(
            EnabledPolicy,
            AttributeRuleEditEvent.Insert,
            existingOwnerValue: null,
            principal: EditPrincipal.Anonymous);

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_AuthenticatedInsert_IsAllowedAndShouldStampOwner()
    {
        var decision = OwnerEditPolicyEvaluator.Evaluate(
            EnabledPolicy,
            AttributeRuleEditEvent.Insert,
            existingOwnerValue: null,
            principal: new EditPrincipal("alice", IsAuthenticated: true, IsAdmin: false));

        decision.IsAllowed.Should().BeTrue();
        OwnerEditPolicyEvaluator.ShouldStampOwnerOnInsert(EnabledPolicy).Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_DisabledPolicy_AllowsAnyPrincipal()
    {
        var disabled = new MetadataV2OwnerEditPolicy { Enabled = false, OwnerField = "owner" };

        var decision = OwnerEditPolicyEvaluator.Evaluate(
            disabled,
            AttributeRuleEditEvent.Delete,
            existingOwnerValue: "bob",
            principal: EditPrincipal.Anonymous);

        decision.IsAllowed.Should().BeTrue();
        OwnerEditPolicyEvaluator.ShouldStampOwnerOnInsert(disabled).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_NullPolicy_IsAllowed()
    {
        OwnerEditPolicyEvaluator.Evaluate(
            policy: null,
            AttributeRuleEditEvent.Update,
            existingOwnerValue: "bob",
            principal: EditPrincipal.Anonymous)
            .IsAllowed.Should().BeTrue();
    }
}
