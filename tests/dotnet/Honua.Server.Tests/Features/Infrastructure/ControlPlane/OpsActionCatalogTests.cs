// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OpsActionCatalogTests
{
    [UnitTest]
    public void Scores_DeriveTiersWithoutAnOverride()
    {
        var redrive = OpsActionCatalog.Actions[OpsActionNames.RedriveDeadLetters];
        redrive.Tier.Should().Be(OpsRemediationTier.Human);
        var safe = redrive with { Reversibility = OpsReversibility.Automatic };
        safe.Tier.Should().Be(OpsRemediationTier.Deterministic);
        OpsActionCatalog.Actions.Where(action => action.Value.IsAutoSafe).Select(action => action.Key)
            .Should().BeEmpty("no current actuator can automatically compensate its effects");
        OpsActionDescriptor[] human =
        [
            safe with { Reversibility = OpsReversibility.Manual },
            safe with { BlastRadius = OpsBlastRadius.Deployment },
            safe with { DataRisk = OpsDataRisk.Recoverable },
            safe with { CostRisk = OpsCostRisk.AboveBound },
            safe with { SecurityRisk = OpsSecurityRisk.ExposureChange },
            safe with { Diagnosis = OpsDiagnosis.Inference },
            safe with { Idempotent = false },
            safe with { RateLimited = false },
        ];
        human.Should().OnlyContain(action => action.Tier == OpsRemediationTier.Human && !action.IsAutoSafe);
        typeof(OpsActionDescriptor).GetProperty(nameof(OpsActionDescriptor.Tier))!.CanWrite.Should().BeFalse();
        OpsActionCatalog.Actions.Values.Should().NotContain(action => action.Tier == OpsRemediationTier.Agent);
    }

    [UnitTheory]
    [InlineData("data.delete")]
    [InlineData("backup.delete")]
    [InlineData("security.disable")]
    [InlineData("iam.widen")]
    [InlineData("network.widen")]
    [InlineData("billing.commit")]
    public void ForbiddenEffects_AreBlockedRegardlessOfApproval(string action)
    {
        var descriptor = OpsActionCatalog.Actions[action];
        descriptor.Tier.Should().Be(OpsRemediationTier.Never);
        descriptor.GuardrailTier.Should().Be(GuardrailTier.Blocked);
        descriptor.IsAutoSafe.Should().BeFalse();
    }

    [UnitTest]
    public void UnknownAction_FailsClosed()
    {
        new OpsActionGuardrailCatalog().TryGetTier("future.unknown", out var tier).Should().BeFalse();
        tier.Should().Be(GuardrailTier.Blocked);
        new OpsActionSafetyCatalog().IsAutoSafe(OperationClass.AdminConfigChange, "future.unknown")
            .Should().BeFalse();
    }
}
