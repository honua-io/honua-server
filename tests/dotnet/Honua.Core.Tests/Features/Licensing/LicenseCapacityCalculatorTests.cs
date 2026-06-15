// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Licensing;

[Protocol(ProtocolNames.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class LicenseCapacityCalculatorTests
{
    [UnitTest]
    public void ComputeServingUnits_WithLargerNode_UsesMaxResourceMultiple()
    {
        var units = LicenseCapacityCalculator.ComputeServingUnits(vCpu: 20, memoryGiB: 96);

        Assert.Equal(3m, units);
    }

    [UnitTest]
    public void ComputeServingUnits_WithServerlessConcurrency_NormalizesToUnits()
    {
        var units = LicenseCapacityCalculator.ComputeServingUnits(serverlessConcurrentExecutions: 17);

        Assert.Equal(3m, units);
    }

    [UnitTest]
    public void ComputeP95_ExcludesSurgeSamples()
    {
        var samples = Enumerable.Range(1, 19)
            .Select(value => new LicenseCapacitySample
            {
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(value),
                ServingUnits = value,
                IsSurge = false
            })
            .Append(new LicenseCapacitySample
            {
                Timestamp = DateTimeOffset.UtcNow.AddHours(1),
                ServingUnits = 100,
                IsSurge = true
            });

        var p95 = LicenseCapacityCalculator.ComputeP95(samples);

        Assert.Equal(19m, p95);
    }

    [UnitTest]
    public void DetermineBandState_WithCurrentAboveBandAndP95InsideBand_ReturnsBurst()
    {
        var terms = new LicenseCapacityTerms { MaxSustainedServingUnits = 4m };

        var state = LicenseCapacityCalculator.DetermineBandState(
            terms,
            currentServingUnits: 5m,
            p95ServingUnits: 4m,
            graceStartedAt: null,
            surgeActive: false,
            meteringGap: false,
            excluded: false,
            now: DateTimeOffset.UtcNow);

        Assert.Equal(LicenseCapacityBandState.Burst, state);
    }

    [UnitTest]
    public void ShouldRefuseRegistration_BeforeGraceExpiry_Allows125PercentBurst()
    {
        var terms = new LicenseCapacityTerms { MaxSustainedServingUnits = 4m };

        var allowed = LicenseCapacityCalculator.ShouldRefuseRegistration(
            terms,
            currentServingUnits: 4m,
            joiningServingUnits: 1m,
            LicenseCapacityBandState.Burst,
            excluded: false);
        var refused = LicenseCapacityCalculator.ShouldRefuseRegistration(
            terms,
            currentServingUnits: 4m,
            joiningServingUnits: 1.1m,
            LicenseCapacityBandState.Burst,
            excluded: false);

        Assert.False(allowed);
        Assert.True(refused);
    }

    [UnitTest]
    public void ShouldRefuseRegistration_AfterGraceExpiry_UsesBaseBand()
    {
        var terms = new LicenseCapacityTerms { MaxSustainedServingUnits = 4m };

        var refused = LicenseCapacityCalculator.ShouldRefuseRegistration(
            terms,
            currentServingUnits: 4m,
            joiningServingUnits: 0.1m,
            LicenseCapacityBandState.GraceExpired,
            excluded: false);

        Assert.True(refused);
    }

    [UnitTest]
    public void ShouldRefuseRegistration_ForExcludedRole_IsAlwaysAllowed()
    {
        var terms = new LicenseCapacityTerms { MaxSustainedServingUnits = 1m };

        var refused = LicenseCapacityCalculator.ShouldRefuseRegistration(
            terms,
            currentServingUnits: 100m,
            joiningServingUnits: 100m,
            LicenseCapacityBandState.GraceExpired,
            excluded: true);

        Assert.False(refused);
        Assert.True(LicenseCapacityCalculator.IsExcludedRole(LicenseDeploymentRole.Staging));
    }
}
