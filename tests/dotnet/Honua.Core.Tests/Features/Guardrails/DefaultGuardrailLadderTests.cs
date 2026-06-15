// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Core.Tests.Features.Guardrails;

/// <summary>
/// Unit tests for the edition guardrail ladder (#1691) covering the
/// edition x operation-class matrix, operator overrides, and unknown classes.
/// </summary>
public class DefaultGuardrailLadderTests
{
    private static DefaultGuardrailLadder CreateLadder(
        HonuaEdition edition = HonuaEdition.Community,
        GuardrailLadderOptions? options = null)
    {
        var entitlements = new Mock<ILicenseEntitlementService>();
        entitlements
            .Setup(service => service.GetSnapshot())
            .Returns(CreateSnapshot(edition));

        return new DefaultGuardrailLadder(
            entitlements.Object,
            Options.Create(options ?? new GuardrailLadderOptions()));
    }

    private static LicenseSnapshot CreateSnapshot(HonuaEdition edition) => new(
        edition,
        IsValid: true,
        LicenseValidationState.Valid,
        ExpiresAt: null,
        LicensedTo: "test",
        LicenseId: "test",
        IssuedAt: null,
        Entitlements: [],
        ActiveEntitlementKeys: new HashSet<string>(),
        SnapshotVersion: 1,
        KeyId: null);

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(HonuaEdition.Community, OperationClass.AdminConfigChange, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Community, OperationClass.Deploy, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Community, OperationClass.MetadataRelease, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Community, OperationClass.Seed, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Pro, OperationClass.AdminConfigChange, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Pro, OperationClass.Deploy, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Pro, OperationClass.MetadataRelease, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Pro, OperationClass.Seed, GuardrailTier.DirectExecute)]
    [InlineData(HonuaEdition.Enterprise, OperationClass.AdminConfigChange, GuardrailTier.RequiresApproval)]
    [InlineData(HonuaEdition.Enterprise, OperationClass.Deploy, GuardrailTier.RequiresApproval)]
    [InlineData(HonuaEdition.Enterprise, OperationClass.MetadataRelease, GuardrailTier.RequiresApproval)]
    [InlineData(HonuaEdition.Enterprise, OperationClass.Seed, GuardrailTier.RequiresApproval)]
    public void Resolve_DefaultPolicy_ReturnsExpectedTierForEditionAndClass(
        HonuaEdition edition,
        OperationClass operationClass,
        GuardrailTier expectedTier)
    {
        var ladder = CreateLadder(edition);

        var decision = ladder.Resolve(operationClass, edition);

        Assert.Equal(expectedTier, decision.Tier);
        Assert.Equal(operationClass, decision.OperationClass);
        Assert.Equal(edition, decision.Edition);
    }

    [UnitTest]
    public void Resolve_UsesActiveEditionFromSnapshot_WhenEditionNotSupplied()
    {
        var ladder = CreateLadder(HonuaEdition.Enterprise);

        var decision = ladder.Resolve(OperationClass.Deploy);

        Assert.Equal(GuardrailTier.RequiresApproval, decision.Tier);
        Assert.Equal(HonuaEdition.Enterprise, decision.Edition);
    }

    [UnitTest]
    public void Resolve_OperatorOverride_TightensCommunityToApproval()
    {
        var options = new GuardrailLadderOptions
        {
            Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Deploy"] = "RequiresApproval"
            }
        };
        var ladder = CreateLadder(HonuaEdition.Community, options);

        var decision = ladder.Resolve(OperationClass.Deploy, HonuaEdition.Community);

        Assert.Equal(GuardrailTier.RequiresApproval, decision.Tier);
        Assert.Equal("operator-override", decision.Source);
    }

    [UnitTest]
    public void Resolve_OperatorOverride_CanBlockOperation()
    {
        var options = new GuardrailLadderOptions
        {
            Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Seed"] = "Blocked"
            }
        };
        var ladder = CreateLadder(HonuaEdition.Pro, options);

        var decision = ladder.Resolve(OperationClass.Seed, HonuaEdition.Pro);

        Assert.Equal(GuardrailTier.Blocked, decision.Tier);
    }

    [UnitTest]
    public void Resolve_InvalidOverrideValue_FallsBackToDefaultPolicy()
    {
        var options = new GuardrailLadderOptions
        {
            Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Deploy"] = "not-a-tier"
            }
        };
        var ladder = CreateLadder(HonuaEdition.Enterprise, options);

        var decision = ladder.Resolve(OperationClass.Deploy, HonuaEdition.Enterprise);

        Assert.Equal(GuardrailTier.RequiresApproval, decision.Tier);
        Assert.Equal("default-policy", decision.Source);
    }

    [UnitTest]
    public void Resolve_UnknownClass_FailsClosedWhenConfigured()
    {
        var ladder = CreateLadder(
            HonuaEdition.Pro,
            new GuardrailLadderOptions { FailClosed = true });

        var decision = ladder.Resolve((OperationClass)999, HonuaEdition.Pro);

        Assert.Equal(GuardrailTier.RequiresApproval, decision.Tier);
        Assert.Equal("fail-closed-unknown-class", decision.Source);
    }

    [UnitTest]
    public void Resolve_UnknownClass_OpensInDevelopmentWhenFailClosedDisabled()
    {
        var ladder = CreateLadder(
            HonuaEdition.Pro,
            new GuardrailLadderOptions { FailClosed = false });

        var decision = ladder.Resolve((OperationClass)999, HonuaEdition.Pro);

        Assert.Equal(GuardrailTier.DirectExecute, decision.Tier);
    }
}
