// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class LicenseCapacityMeterTests
{
    [UnitTest]
    public async Task RegisterInstance_WhenJoiningWouldExceedBurstCeiling_RefusesNewRegistrationOnly()
    {
        var meter = CreateMeter(maxSustainedUnits: 4m);

        var first = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "prod-a",
            ServingUnits = 5m,
            DeploymentRole = LicenseDeploymentRole.Production,
            Topology = LicenseServingTopology.ReplicaSet
        });
        var second = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "prod-b",
            ServingUnits = 0.1m,
            DeploymentRole = LicenseDeploymentRole.Production,
            Topology = LicenseServingTopology.ReplicaSet
        });
        var state = await meter.GetCapacityStateAsync();

        Assert.True(first.IsAccepted);
        Assert.False(second.IsAccepted);
        Assert.Equal(5m, state.CurrentServingUnits);
        Assert.Equal(1, state.LiveInstanceCount);
    }

    [UnitTest]
    public async Task RegisterInstance_WithNonProductionRole_DoesNotCountTowardBand()
    {
        var meter = CreateMeter(maxSustainedUnits: 1m);

        var decision = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "staging-a",
            ServingUnits = 50m,
            DeploymentRole = LicenseDeploymentRole.Staging,
            Topology = LicenseServingTopology.ReplicaSet
        });
        var state = await meter.GetCapacityStateAsync();

        Assert.True(decision.IsAccepted);
        Assert.Equal(0m, state.CurrentServingUnits);
        Assert.Equal(1, state.ExcludedInstanceCount);
    }

    [UnitTest]
    public async Task RegisterInstance_WithSurgeMode_AllowsRegistrationAboveBurstCeiling()
    {
        var meter = CreateMeter(maxSustainedUnits: 1m);

        var surge = await meter.SetSurgeModeAsync(enabled: true, reason: "incident response");
        var decision = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
        {
            InstanceId = "prod-surge",
            ServingUnits = 10m,
            DeploymentRole = LicenseDeploymentRole.Production,
            Topology = LicenseServingTopology.Serverless
        });

        Assert.True(surge.Surge.IsActive);
        Assert.Equal(LicenseCapacityBandState.Surge, surge.State);
        Assert.True(decision.IsAccepted);
        Assert.Equal(10m, decision.State.CurrentServingUnits);
    }

    [UnitTest]
    public async Task GetCapacityState_WithActiveSurge_IncludesActiveWindowInAllowanceAccounting()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var meter = CreateMeter(maxSustainedUnits: 1m, clock);

        await meter.SetSurgeModeAsync(enabled: true, reason: "planned load test");
        clock.Advance(TimeSpan.FromHours(12));
        var state = await meter.GetCapacityStateAsync();

        Assert.True(state.Surge.IsActive);
        Assert.Equal(0.5m, state.Surge.UsedDaysThisYear);
        Assert.Equal(13.5m, state.Surge.RemainingDaysThisYear);
    }

    private static LicenseCapacityMeter CreateMeter(decimal maxSustainedUnits, TimeProvider? timeProvider = null)
    {
        var license = new TestLicenseEntitlementService(
            HonuaEdition.Enterprise,
            capacityTerms: new LicenseCapacityTerms
            {
                MaxSustainedServingUnits = maxSustainedUnits,
                AnnualSurgeDays = 14,
                SurgeAllowance = LicenseCapacitySurgeAllowances.Standard
            });
        return new LicenseCapacityMeter(
            license,
            Options.Create(new LicenseCapacityOptions
            {
                RegistrationEnabled = false,
                InstanceId = "local-test",
                ServingUnits = 1m
            }),
            timeProvider ?? TimeProvider.System,
            NullLogger<LicenseCapacityMeter>.Instance);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan timeSpan) => _utcNow += timeSpan;
    }
}
