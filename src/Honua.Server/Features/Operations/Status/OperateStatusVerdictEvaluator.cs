// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Operations.Status;

public enum OperateOverallStatus
{
    Healthy = 0,

    Degraded = 1,

    Unhealthy = 2,
}

public readonly record struct OperateStatusSignals(
    string HealthRollupStatus,
    IReadOnlyList<string> CriticalFindingRules,
    int ParkedDeploys,
    long AlertDeadLettered,
    bool AlertDispatchImpaired);

// Warning and info findings do not by themselves downgrade the verdict.
public static class OperateStatusVerdictEvaluator
{
    public static (OperateOverallStatus Status, IReadOnlyList<string> Reasons) Evaluate(OperateStatusSignals signals)
    {
        var reasons = new List<string>();
        var rollup = signals.HealthRollupStatus;

        // Rule 1: a critical dependency is down.
        if (string.Equals(rollup, "Unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("health-rollup:Unhealthy");
            return (OperateOverallStatus.Unhealthy, reasons);
        }

        // Rule 2: degradation signals.
        if (string.Equals(rollup, "Degraded", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("health-rollup:Degraded");
        }

        foreach (var rule in signals.CriticalFindingRules ?? [])
        {
            reasons.Add($"critical-finding:{rule}");
        }

        if (signals.ParkedDeploys > 0)
        {
            reasons.Add("deploy-parked");
        }

        if (signals.AlertDeadLettered > 0)
        {
            reasons.Add("alert-dead-lettered");
        }

        if (signals.AlertDispatchImpaired)
        {
            reasons.Add("alert-dispatch-impaired");
        }

        var status = reasons.Count > 0 ? OperateOverallStatus.Degraded : OperateOverallStatus.Healthy;
        return (status, reasons);
    }

    public static string ToWire(OperateOverallStatus status) => status switch
    {
        OperateOverallStatus.Healthy => "healthy",
        OperateOverallStatus.Degraded => "degraded",
        OperateOverallStatus.Unhealthy => "unhealthy",
        _ => "unhealthy",
    };
}
