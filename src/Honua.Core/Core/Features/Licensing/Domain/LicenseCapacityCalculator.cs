// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

public static class LicenseCapacityCalculator
{
    public const decimal VcpuPerServingUnit = 8m;

    public const decimal MemoryGiBPerServingUnit = 32m;

    public const decimal BurstMultiplier = 1.25m;

    public const int GracePeriodDays = 30;

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

    public static decimal GetBurstCeiling(LicenseCapacityTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms.MaxSustainedServingUnits * BurstMultiplier;
    }

    public static bool IsExcludedRole(LicenseDeploymentRole role) => role != LicenseDeploymentRole.Production;

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
