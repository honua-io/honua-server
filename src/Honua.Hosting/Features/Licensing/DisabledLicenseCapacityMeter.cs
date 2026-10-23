// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

/// <summary>
/// Inert capacity facade used when licensing is disabled. No registrations, samples or surge
/// state are retained, and no Redis connection or background heartbeat is created.
/// </summary>
internal sealed class DisabledLicenseCapacityMeter : ILicenseCapacityMeter
{
    private static readonly LicenseCapacityState _state = new()
    {
        MeteringEnabled = false,
        State = LicenseCapacityBandState.Disabled,
        CurrentServingUnits = 0,
        P95ServingUnits = 0,
        BurstMultiplier = 0,
        GracePeriodDays = 0,
        RegistrationEnforced = false,
        RedisConfigured = false,
        RedisCoordinated = false,
        MeteringGap = false,
        LocalDeploymentRole = LicenseDeploymentRole.Production,
        LocalTopology = LicenseServingTopology.SingleNode,
        LocalServingUnits = 0,
        LocalRoleExcluded = true,
        LiveInstanceCount = 0,
        ProductionInstanceCount = 0,
        ExcludedInstanceCount = 0,
        Surge = new LicenseSurgeModeState { IsActive = false },
        Warning80Percent = false,
        Warning100Percent = false,
        Messages = ["Licensing and capacity metering are disabled."]
    };

    public Task<LicenseCapacityRegistrationDecision> RegisterInstanceAsync(
        LicenseCapacityRegistrationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new LicenseCapacityRegistrationDecision
        {
            IsAccepted = true,
            Reason = "Licensing and capacity metering are disabled; no registration was recorded.",
            State = _state
        });

    public Task<LicenseCapacityState> GetCapacityStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state);

    public Task<LicenseCapacityState> SetSurgeModeAsync(bool enabled, string? reason, CancellationToken cancellationToken = default)
        => Task.FromResult(_state);
}
