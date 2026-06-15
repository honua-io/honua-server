// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

/// <summary>
/// Shared capacity-band policy calculations for licensing.
/// </summary>
public static class LicenseCapacityCalculator
{
    /// <summary>
    /// One serving unit covers up to this many virtual CPUs.
    /// </summary>
    public const decimal VcpuPerServingUnit = 8m;

    /// <summary>
    /// One serving unit covers up to this many GiB of memory.
    /// </summary>
    public const decimal MemoryGiBPerServingUnit = 32m;

    /// <summary>
    /// Free burst headroom above the sustained capacity band.
    /// </summary>
    public const decimal BurstMultiplier = 1.25m;

    /// <summary>
    /// Sustained-growth grace period after p95 exceeds the licensed band.
    /// </summary>
    public const int GracePeriodDays = 30;

    /// <summary>
    /// Computes serving units from explicit units or topology resources.
    /// </summary>
    /// <param name="explicitServingUnits">Operator-supplied serving-unit weight.</param>
    /// <param name="vCpu">Virtual CPU count for the serving instance.</param>
    /// <param name="memoryGiB">Memory in GiB for the serving instance.</param>
    /// <param name="serverlessConcurrentExecutions">Serverless concurrent execution weight.</param>
    /// <returns>Serving units, minimum one.</returns>
    public static decimal ComputeServingUnits(
        decimal? explicitServingUnits = null,
        double? vCpu = null,
        double? memoryGiB = null,
        int? serverlessConcurrentExecutions = null)
    {
        if (explicitServingUnits is > 0m)
        {
            return explicitServingUnits.Value;
        }

        var cpuUnits = vCpu is > 0d
            ? Math.Ceiling((decimal)vCpu.Value / VcpuPerServingUnit)
            : 0m;
        var memoryUnits = memoryGiB is > 0d
            ? Math.Ceiling((decimal)memoryGiB.Value / MemoryGiBPerServingUnit)
            : 0m;
        var executionUnits = serverlessConcurrentExecutions is > 0
            ? Math.Ceiling(serverlessConcurrentExecutions.Value / VcpuPerServingUnit)
            : 0m;

        return Math.Max(1m, Math.Max(cpuUnits, Math.Max(memoryUnits, executionUnits)));
    }

    /// <summary>
    /// Computes nearest-rank p95 from samples, excluding declared surge windows.
    /// </summary>
    /// <param name="samples">Capacity samples.</param>
    /// <returns>P95 serving units, or zero when no non-surge samples exist.</returns>
    public static decimal ComputeP95(IEnumerable<LicenseCapacitySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples
            .Where(sample => !sample.IsSurge)
            .Select(sample => sample.ServingUnits)
            .Order()
            .ToArray();

        if (ordered.Length == 0)
        {
            return 0m;
        }

        var rank = (int)Math.Ceiling(ordered.Length * 0.95d) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }

    /// <summary>
    /// Gets the 125 percent burst ceiling for a capacity band.
    /// </summary>
    /// <param name="terms">Capacity terms.</param>
    /// <returns>Burst ceiling in serving units.</returns>
    public static decimal GetBurstCeiling(LicenseCapacityTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms.MaxSustainedServingUnits * BurstMultiplier;
    }

    /// <summary>
    /// Gets whether a deployment role is excluded from production band fit.
    /// </summary>
    /// <param name="role">Deployment role.</param>
    /// <returns>True when the role is not production.</returns>
    public static bool IsExcludedRole(LicenseDeploymentRole role) => role != LicenseDeploymentRole.Production;

    /// <summary>
    /// Determines the capacity band state from live and p95 usage.
    /// </summary>
    /// <param name="terms">Capacity terms, when configured.</param>
    /// <param name="currentServingUnits">Current live production serving units.</param>
    /// <param name="p95ServingUnits">Rolling p95 production serving units.</param>
    /// <param name="graceStartedAt">Current grace-clock start, when active.</param>
    /// <param name="surgeActive">Whether declared surge mode is active.</param>
    /// <param name="meteringGap">Whether coordinated metering is unavailable.</param>
    /// <param name="excluded">Whether the local role is excluded from band accounting.</param>
    /// <param name="now">Current timestamp.</param>
    /// <returns>Capacity band state.</returns>
    public static LicenseCapacityBandState DetermineBandState(
        LicenseCapacityTerms? terms,
        decimal currentServingUnits,
        decimal p95ServingUnits,
        DateTimeOffset? graceStartedAt,
        bool surgeActive,
        bool meteringGap,
        bool excluded,
        DateTimeOffset now)
    {
        if (meteringGap)
        {
            return LicenseCapacityBandState.MeteringGap;
        }

        if (terms is null)
        {
            return LicenseCapacityBandState.NotConfigured;
        }

        if (excluded)
        {
            return LicenseCapacityBandState.Excluded;
        }

        if (surgeActive)
        {
            return LicenseCapacityBandState.Surge;
        }

        var baseCeiling = terms.MaxSustainedServingUnits;
        if (p95ServingUnits > baseCeiling)
        {
            if (graceStartedAt.HasValue &&
                now - graceStartedAt.Value >= TimeSpan.FromDays(GracePeriodDays))
            {
                return LicenseCapacityBandState.GraceExpired;
            }

            return LicenseCapacityBandState.Grace;
        }

        if (currentServingUnits > baseCeiling)
        {
            return LicenseCapacityBandState.Burst;
        }

        return p95ServingUnits >= baseCeiling * 0.8m
            ? LicenseCapacityBandState.ApproachingBand
            : LicenseCapacityBandState.Normal;
    }

    /// <summary>
    /// Determines whether a new production registration should be refused.
    /// </summary>
    /// <param name="terms">Capacity terms, when configured.</param>
    /// <param name="currentServingUnits">Current live production serving units, excluding the joining instance.</param>
    /// <param name="joiningServingUnits">Serving units for the joining instance.</param>
    /// <param name="state">Current capacity band state.</param>
    /// <param name="excluded">Whether the joining instance is excluded from band accounting.</param>
    /// <returns>True when the joining registration is above the active ceiling.</returns>
    public static bool ShouldRefuseRegistration(
        LicenseCapacityTerms? terms,
        decimal currentServingUnits,
        decimal joiningServingUnits,
        LicenseCapacityBandState state,
        bool excluded)
    {
        if (terms is null ||
            excluded ||
            state is LicenseCapacityBandState.NotConfigured or
                LicenseCapacityBandState.Excluded or
                LicenseCapacityBandState.Surge or
                LicenseCapacityBandState.MeteringGap)
        {
            return false;
        }

        var ceiling = state == LicenseCapacityBandState.GraceExpired
            ? terms.MaxSustainedServingUnits
            : GetBurstCeiling(terms);

        return currentServingUnits + joiningServingUnits > ceiling;
    }
}
