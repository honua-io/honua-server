// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Tests.Features.Authorization;

/// <summary>
/// Unit tests for the AI-operations guardrail ladder (#1631): Community runs
/// agent operations directly, Pro inserts the validation layer (plan/dry-run/
/// validate + pre-change snapshot), and Enterprise adds approval + policy/audit.
/// </summary>
public sealed class EditionAgentGuardrailPolicyTests
{
    private static AgentOperationDescriptor MutatingChange(bool supportsSnapshot = true, bool destructive = false) => new()
    {
        OperationId = "spec.apply",
        Protocol = "spec",
        IsMutating = true,
        IsDestructive = destructive,
        SupportsSnapshot = supportsSnapshot,
    };

    private static EditionAgentGuardrailPolicy PolicyFor(HonuaEdition edition)
        => new(new StubEntitlementService(edition));

    [Theory]
    [InlineData(HonuaEdition.Community, AgentGuardrailLevel.Direct)]
    [InlineData(HonuaEdition.Pro, AgentGuardrailLevel.Validated)]
    [InlineData(HonuaEdition.Enterprise, AgentGuardrailLevel.Approval)]
    public void LevelFor_MapsEditionToLadder(HonuaEdition edition, AgentGuardrailLevel expected)
    {
        PolicyFor(edition).LevelFor(edition).Should().Be(expected);
    }

    [Fact]
    public void Evaluate_Community_RunsDirectlyWithNoGuardrails()
    {
        var decision = PolicyFor(HonuaEdition.Community).Evaluate(MutatingChange());

        decision.Level.Should().Be(AgentGuardrailLevel.Direct);
        decision.Edition.Should().Be(HonuaEdition.Community);
        decision.HasGuardrails.Should().BeFalse();
        decision.RequiresValidation.Should().BeFalse();
        decision.RequiresSnapshot.Should().BeFalse();
        decision.RequiresApproval.Should().BeFalse();
        decision.RequiresPolicyAudit.Should().BeFalse();
        decision.ReasonCodes.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_Pro_RequiresValidationLayerAndSnapshotButNotApproval()
    {
        var decision = PolicyFor(HonuaEdition.Pro).Evaluate(MutatingChange());

        decision.Level.Should().Be(AgentGuardrailLevel.Validated);
        decision.HasGuardrails.Should().BeTrue();
        decision.RequiresValidation.Should().BeTrue();
        decision.RequiresSnapshot.Should().BeTrue();
        decision.SnapshotUnsupported.Should().BeFalse();
        decision.RequiresApproval.Should().BeFalse();
        decision.RequiresPolicyAudit.Should().BeFalse();
        decision.ReasonCodes.Should().Contain("agent-change-requires-validation");
        decision.ReasonCodes.Should().Contain("pre-change-snapshot-required");
        decision.ReasonCodes.Should().NotContain("agent-change-requires-approval");
    }

    [Fact]
    public void Evaluate_Enterprise_RequiresApprovalAndPolicyAuditOnTopOfValidation()
    {
        var decision = PolicyFor(HonuaEdition.Enterprise).Evaluate(MutatingChange());

        decision.Level.Should().Be(AgentGuardrailLevel.Approval);
        decision.HasGuardrails.Should().BeTrue();
        decision.RequiresValidation.Should().BeTrue();
        decision.RequiresSnapshot.Should().BeTrue();
        decision.RequiresApproval.Should().BeTrue();
        decision.RequiresPolicyAudit.Should().BeTrue();
        decision.ReasonCodes.Should().Contain("agent-change-requires-validation");
        decision.ReasonCodes.Should().Contain("agent-change-requires-approval");
        decision.ReasonCodes.Should().Contain("agent-action-policy-audit");
    }

    [Fact]
    public void Evaluate_Enterprise_DestructiveChange_EmitsDestructiveApprovalReason()
    {
        var decision = PolicyFor(HonuaEdition.Enterprise).Evaluate(MutatingChange(destructive: true));

        decision.RequiresApproval.Should().BeTrue();
        decision.ReasonCodes.Should().Contain("destructive-agent-change-requires-approval");
    }

    [Fact]
    public void Evaluate_Pro_SnapshotUnsupportedOperation_FlagsUnsatisfiableGate()
    {
        var decision = PolicyFor(HonuaEdition.Pro).Evaluate(MutatingChange(supportsSnapshot: false));

        decision.RequiresSnapshot.Should().BeTrue();
        decision.SnapshotUnsupported.Should().BeTrue();
        decision.ReasonCodes.Should().Contain("pre-change-snapshot-unsupported");
        decision.ReasonCodes.Should().NotContain("pre-change-snapshot-required");
    }

    [Theory]
    [InlineData(HonuaEdition.Community)]
    [InlineData(HonuaEdition.Pro)]
    [InlineData(HonuaEdition.Enterprise)]
    public void Evaluate_NonMutatingOperation_IsNeverGated(HonuaEdition edition)
    {
        var read = new AgentOperationDescriptor
        {
            OperationId = "honua_dry_run_plan",
            Protocol = "mcp",
            IsMutating = false,
        };

        var decision = PolicyFor(edition).Evaluate(read);

        decision.HasGuardrails.Should().BeFalse();
        decision.RequiresValidation.Should().BeFalse();
        decision.RequiresApproval.Should().BeFalse();
        decision.Edition.Should().Be(edition);
    }

    private sealed class StubEntitlementService(HonuaEdition edition) : ILicenseEntitlementService
    {
        public LicenseSnapshot GetSnapshot() => new(
            Edition: edition,
            IsValid: true,
            ValidationState: LicenseValidationState.Valid,
            ExpiresAt: null,
            LicensedTo: "Test",
            LicenseId: "test",
            IssuedAt: null,
            Entitlements: [],
            ActiveEntitlementKeys: new HashSet<string>(),
            SnapshotVersion: 1,
            KeyId: null);

        public LicenseEntitlementDecision CheckEntitlement(string entitlementKey) => new(
            entitlementKey,
            IsActive: edition != HonuaEdition.Community,
            Edition: edition,
            ValidationState: LicenseValidationState.Valid,
            RequiredEdition: null,
            UpgradeMessage: "upgrade");
    }
}
