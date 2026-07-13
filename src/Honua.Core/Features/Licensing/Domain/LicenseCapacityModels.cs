// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

/// <summary>
/// Deployment roles used by the licensing capacity meter.
/// </summary>
public enum LicenseDeploymentRole
{
    /// <summary>
    /// Production serving capacity that counts toward the licensed band.
    /// </summary>
    Production = 0,

    /// <summary>
    /// Developer or local capacity excluded from the licensed band.
    /// </summary>
    Development = 1,

    /// <summary>
    /// Automated test capacity excluded from the licensed band.
    /// </summary>
    Test = 2,

    /// <summary>
    /// Staging or pre-production capacity excluded from the licensed band.
    /// </summary>
    Staging = 3,

    /// <summary>
    /// Cold disaster-recovery capacity excluded from the licensed band.
    /// </summary>
    ColdDr = 4,
}

/// <summary>
/// Runtime topology marker reported by a serving instance.
/// </summary>
public enum LicenseServingTopology
{
    /// <summary>
    /// A single serving node without distributed coordination.
    /// </summary>
    SingleNode = 0,

    /// <summary>
    /// A virtual machine or bare-metal host.
    /// </summary>
    VirtualMachine = 1,

    /// <summary>
    /// A Kubernetes, ECS, or similar replica-managed topology.
    /// </summary>
    ReplicaSet = 2,

    /// <summary>
    /// A serverless warm execution environment.
    /// </summary>
    Serverless = 3,
}

/// <summary>
/// Capacity-band state reported by the license meter.
/// </summary>
public enum LicenseCapacityBandState
{
    /// <summary>
    /// No capacity band is encoded in the active license.
    /// </summary>
    NotConfigured = 0,

    /// <summary>
    /// The current deployment role is excluded from capacity accounting.
    /// </summary>
    Excluded = 1,

    /// <summary>
    /// Capacity is inside the licensed band.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// P95 capacity is at or above the 80 percent warning threshold.
    /// </summary>
    ApproachingBand = 3,

    /// <summary>
    /// Live capacity is above the base band but inside the 125 percent burst ceiling.
    /// </summary>
    Burst = 4,

    /// <summary>
    /// P95 capacity is above the base band and the sustained-growth grace clock is active.
    /// </summary>
    Grace = 5,

    /// <summary>
    /// The sustained-growth grace period has expired.
    /// </summary>
    GraceExpired = 6,

    /// <summary>
    /// Operator-declared surge mode is active and p95 samples are masked.
    /// </summary>
    Surge = 7,

    /// <summary>
    /// Capacity enforcement is suspended because the coordinated meter is unavailable.
    /// </summary>
    MeteringGap = 8,
}

/// <summary>
/// Constants for license surge allowance names.
/// </summary>
public static class LicenseCapacitySurgeAllowances
{
    /// <summary>
    /// Standard allowance for ordinary paid licenses.
    /// </summary>
    public const string Standard = "standard";

    /// <summary>
    /// High allowance for licenses with a purchased surge pack.
    /// </summary>
    public const string High = "high";

    /// <summary>
    /// Unlimited allowance for licenses with uncapped surge rights.
    /// </summary>
    public const string Unlimited = "unlimited";
}

/// <summary>
/// Capacity-band terms encoded in a signed license.
/// </summary>
public sealed class LicenseCapacityTerms
{
    /// <summary>
    /// Maximum sustained p95 serving units included in the license.
    /// </summary>
    public required decimal MaxSustainedServingUnits { get; init; }

    /// <summary>
    /// Number of declared surge days available per calendar year. Null means unlimited.
    /// </summary>
    public int? AnnualSurgeDays { get; init; } = 14;

    /// <summary>
    /// Human-readable surge allowance tier.
    /// </summary>
    public string SurgeAllowance { get; init; } = LicenseCapacitySurgeAllowances.Standard;
}

/// <summary>
/// One live serving instance as seen by the capacity meter.
/// </summary>
public sealed class LicenseCapacityInstance
{
    /// <summary>
    /// Stable instance identifier.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Serving-unit weight for this instance.
    /// </summary>
    public required decimal ServingUnits { get; init; }

    /// <summary>
    /// Deployment role reported by this instance.
    /// </summary>
    public required LicenseDeploymentRole DeploymentRole { get; init; }

    /// <summary>
    /// Runtime topology reported by this instance.
    /// </summary>
    public required LicenseServingTopology Topology { get; init; }

    /// <summary>
    /// Last heartbeat timestamp known to the meter.
    /// </summary>
    public required DateTimeOffset LastHeartbeatAt { get; init; }

    /// <summary>
    /// Whether this instance contributes to the production band calculation.
    /// </summary>
    public required bool CountsTowardBand { get; init; }

    /// <summary>
    /// The binary version this instance advertised, or <see langword="null"/> when the instance did not
    /// report one (for example, an older binary from before version advertising shipped). Consumed by
    /// the migration node-version barrier (#2812) as a live cluster-skew signal.
    /// </summary>
    public string? BinaryVersion { get; init; }
}

/// <summary>
/// One sampled capacity value for p95 computation.
/// </summary>
public sealed class LicenseCapacitySample
{
    /// <summary>
    /// Sample timestamp.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Concurrent serving units observed at this sample.
    /// </summary>
    public required decimal ServingUnits { get; init; }

    /// <summary>
    /// Whether this sample occurred during declared surge mode and is excluded from p95.
    /// </summary>
    public required bool IsSurge { get; init; }
}

/// <summary>
/// Declared surge-mode state and annual allowance accounting.
/// </summary>
public sealed class LicenseSurgeModeState
{
    /// <summary>
    /// Whether surge mode is active.
    /// </summary>
    public required bool IsActive { get; init; }

    /// <summary>
    /// When the active surge window started.
    /// </summary>
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>
    /// Operator-supplied activation reason.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Annual surge-day allowance. Null means unlimited.
    /// </summary>
    public int? AnnualAllowanceDays { get; init; }

    /// <summary>
    /// Surge days used in the current calendar year, including the active window.
    /// </summary>
    public decimal UsedDaysThisYear { get; init; }

    /// <summary>
    /// Remaining surge days in the current calendar year. Null means unlimited.
    /// </summary>
    public decimal? RemainingDaysThisYear { get; init; }
}

/// <summary>
/// Current capacity meter state shown in the admin dashboard and used by admission checks.
/// </summary>
public sealed class LicenseCapacityState
{
    /// <summary>
    /// Capacity-band state.
    /// </summary>
    public required LicenseCapacityBandState State { get; init; }

    /// <summary>
    /// Capacity terms encoded in the active license, when present.
    /// </summary>
    public LicenseCapacityTerms? Terms { get; init; }

    /// <summary>
    /// Current live production serving units.
    /// </summary>
    public required decimal CurrentServingUnits { get; init; }

    /// <summary>
    /// Rolling p95 production serving units, excluding surge samples.
    /// </summary>
    public required decimal P95ServingUnits { get; init; }

    /// <summary>
    /// The 125 percent burst ceiling derived from the base band.
    /// </summary>
    public decimal? BurstCeilingServingUnits { get; init; }

    /// <summary>
    /// Burst multiplier applied to the base band.
    /// </summary>
    public required decimal BurstMultiplier { get; init; }

    /// <summary>
    /// Sustained-growth grace period in days.
    /// </summary>
    public required int GracePeriodDays { get; init; }

    /// <summary>
    /// When the current sustained-growth grace clock started.
    /// </summary>
    public DateTimeOffset? GraceStartedAt { get; init; }

    /// <summary>
    /// When the current sustained-growth grace period expires.
    /// </summary>
    public DateTimeOffset? GraceExpiresAt { get; init; }

    /// <summary>
    /// Whether new production registrations above the active ceiling are refused.
    /// </summary>
    public required bool RegistrationEnforced { get; init; }

    /// <summary>
    /// Whether Redis coordination is configured.
    /// </summary>
    public required bool RedisConfigured { get; init; }

    /// <summary>
    /// Whether Redis coordination is being used for this state.
    /// </summary>
    public required bool RedisCoordinated { get; init; }

    /// <summary>
    /// Whether enforcement is suspended due to an unavailable coordinated meter.
    /// </summary>
    public required bool MeteringGap { get; init; }

    /// <summary>
    /// Role reported by the local serving instance.
    /// </summary>
    public required LicenseDeploymentRole LocalDeploymentRole { get; init; }

    /// <summary>
    /// Topology reported by the local serving instance.
    /// </summary>
    public required LicenseServingTopology LocalTopology { get; init; }

    /// <summary>
    /// Serving-unit weight for the local instance.
    /// </summary>
    public required decimal LocalServingUnits { get; init; }

    /// <summary>
    /// Whether the local instance is excluded from production band accounting.
    /// </summary>
    public required bool LocalRoleExcluded { get; init; }

    /// <summary>
    /// Number of live heartbeat records known to the meter.
    /// </summary>
    public required int LiveInstanceCount { get; init; }

    /// <summary>
    /// Number of live production instances counted toward the band.
    /// </summary>
    public required int ProductionInstanceCount { get; init; }

    /// <summary>
    /// Number of live non-production instances excluded from the band.
    /// </summary>
    public required int ExcludedInstanceCount { get; init; }

    /// <summary>
    /// Surge mode and allowance state.
    /// </summary>
    public required LicenseSurgeModeState Surge { get; init; }

    /// <summary>
    /// Whether p95 usage has reached the 80 percent warning threshold.
    /// </summary>
    public required bool Warning80Percent { get; init; }

    /// <summary>
    /// Whether p95 usage is above the licensed base band.
    /// </summary>
    public required bool Warning100Percent { get; init; }

    /// <summary>
    /// Safe operator-facing state messages.
    /// </summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>
/// Request to register or refresh a serving instance in the capacity meter.
/// </summary>
public sealed class LicenseCapacityRegistrationRequest
{
    /// <summary>
    /// Stable serving instance identifier.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Serving-unit weight for the instance.
    /// </summary>
    public required decimal ServingUnits { get; init; }

    /// <summary>
    /// Deployment role for the instance.
    /// </summary>
    public required LicenseDeploymentRole DeploymentRole { get; init; }

    /// <summary>
    /// Runtime topology for the instance.
    /// </summary>
    public required LicenseServingTopology Topology { get; init; }

    /// <summary>
    /// Whether this call is a heartbeat refresh for an already admitted instance.
    /// </summary>
    public bool IsRefresh { get; init; }

    /// <summary>
    /// The binary version this serving instance is running, advertised to the coordinated meter so the
    /// migration node-version barrier (#2812) can observe live cluster version skew. Optional.
    /// </summary>
    public string? BinaryVersion { get; init; }
}

/// <summary>
/// Result of capacity registration.
/// </summary>
public sealed class LicenseCapacityRegistrationDecision
{
    /// <summary>
    /// Whether the serving instance was admitted.
    /// </summary>
    public required bool IsAccepted { get; init; }

    /// <summary>
    /// Safe operator-facing reason for the decision.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Capacity state used for the decision.
    /// </summary>
    public required LicenseCapacityState State { get; init; }
}
