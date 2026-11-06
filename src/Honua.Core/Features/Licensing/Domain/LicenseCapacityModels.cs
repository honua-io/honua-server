// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

public enum LicenseDeploymentRole
{
    Production = 0,

    Development = 1,

    Test = 2,

    Staging = 3,

    ColdDr = 4,
}

public enum LicenseServingTopology
{
    SingleNode = 0,

    VirtualMachine = 1,

    ReplicaSet = 2,

    Serverless = 3,
}

public enum LicenseCapacityBandState
{
    NotConfigured = 0,

    Excluded = 1,

    Normal = 2,

    ApproachingBand = 3,

    Burst = 4,

    Grace = 5,

    GraceExpired = 6,

    Surge = 7,

    // Capacity enforcement is suspended when the coordinated meter is unavailable.
    MeteringGap = 8,

    Disabled = 9,
}

public static class LicenseCapacitySurgeAllowances
{
    public const string Standard = "standard";

    public const string High = "high";

    public const string Unlimited = "unlimited";
}

public sealed class LicenseCapacityTerms
{
    public required decimal MaxSustainedServingUnits { get; init; }

    public int? AnnualSurgeDays { get; init; } = 14;

    public string SurgeAllowance { get; init; } = LicenseCapacitySurgeAllowances.Standard;
}

public sealed class LicenseCapacityInstance
{
    public required string InstanceId { get; init; }

    public required decimal ServingUnits { get; init; }

    public required LicenseDeploymentRole DeploymentRole { get; init; }

    public required LicenseServingTopology Topology { get; init; }

    public required DateTimeOffset LastHeartbeatAt { get; init; }

    public required bool CountsTowardBand { get; init; }
}

public sealed class LicenseCapacitySample
{
    public required DateTimeOffset Timestamp { get; init; }

    public required decimal ServingUnits { get; init; }

    public required bool IsSurge { get; init; }
}

public sealed class LicenseSurgeModeState
{
    public required bool IsActive { get; init; }

    public DateTimeOffset? ActivatedAt { get; init; }

    public string? Reason { get; init; }

    public int? AnnualAllowanceDays { get; init; }

    public decimal UsedDaysThisYear { get; init; }

    public decimal? RemainingDaysThisYear { get; init; }
}

public sealed class LicenseCapacityState
{
    public bool MeteringEnabled { get; init; } = true;

    public required LicenseCapacityBandState State { get; init; }

    public LicenseCapacityTerms? Terms { get; init; }

    public required decimal CurrentServingUnits { get; init; }

    public required decimal P95ServingUnits { get; init; }

    public decimal? BurstCeilingServingUnits { get; init; }

    public required decimal BurstMultiplier { get; init; }

    public required int GracePeriodDays { get; init; }

    public DateTimeOffset? GraceStartedAt { get; init; }

    public DateTimeOffset? GraceExpiresAt { get; init; }

    public required bool RegistrationEnforced { get; init; }

    public required bool RedisConfigured { get; init; }

    public required bool RedisCoordinated { get; init; }

    public required bool MeteringGap { get; init; }

    public required LicenseDeploymentRole LocalDeploymentRole { get; init; }

    public required LicenseServingTopology LocalTopology { get; init; }

    public required decimal LocalServingUnits { get; init; }

    public required bool LocalRoleExcluded { get; init; }

    public required int LiveInstanceCount { get; init; }

    public required int ProductionInstanceCount { get; init; }

    public required int ExcludedInstanceCount { get; init; }

    public required LicenseSurgeModeState Surge { get; init; }

    public required bool Warning80Percent { get; init; }

    public required bool Warning100Percent { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];
}

public sealed class LicenseCapacityRegistrationRequest
{
    public required string InstanceId { get; init; }

    public required decimal ServingUnits { get; init; }

    public required LicenseDeploymentRole DeploymentRole { get; init; }

    public required LicenseServingTopology Topology { get; init; }

    public bool IsRefresh { get; init; }
}

public sealed class LicenseCapacityRegistrationDecision
{
    public required bool IsAccepted { get; init; }

    public required string Reason { get; init; }

    public required LicenseCapacityState State { get; init; }
}
