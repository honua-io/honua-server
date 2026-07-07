// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Operations.Status;

/// <summary>The server-computed overall operational verdict.</summary>
public enum OperateOverallStatus
{
    /// <summary>All observed domains are clear.</summary>
    Healthy = 0,

    /// <summary>Serving continues but at least one domain needs attention.</summary>
    Degraded = 1,

    /// <summary>A critical dependency is down; the system is not fully serving.</summary>
    Unhealthy = 2,
}

/// <summary>
/// The signals the verdict is computed from. Kept as a plain input record so the verdict rules are
/// side-effect-free and unit-testable without any DI, HTTP, or store access.
/// </summary>
/// <param name="HealthRollupStatus">
/// The ASP.NET Core health-check roll-up status string (<c>Healthy</c>/<c>Degraded</c>/<c>Unhealthy</c>).
/// </param>
/// <param name="CriticalFindingRules">The kebab-case rule ids of active Critical findings (drives reasons).</param>
/// <param name="ParkedDeploys">Count of deploy operations parked in ManualInterventionRequired.</param>
/// <param name="AlertDeadLettered">Dead-lettered alert dispatch count (0 when unknown/none).</param>
/// <param name="AlertDispatchImpaired">Whether the alert dispatcher is enabled but not running, or its storage poll is failing.</param>
/// <param name="SloErrorBudgetExhausted">Whether a configured availability SLO has exhausted its error budget over the window.</param>
public readonly record struct OperateStatusSignals(
    string HealthRollupStatus,
    IReadOnlyList<string> CriticalFindingRules,
    int ParkedDeploys,
    long AlertDeadLettered,
    bool AlertDispatchImpaired,
    bool SloErrorBudgetExhausted);

/// <summary>
/// Pure, documented verdict rules for the aggregated operate status. The verdict lives server-side so
/// every caller sees the same authoritative "is the system healthy" answer instead of inventing one.
/// </summary>
/// <remarks>
/// Rules (evaluated in order):
/// <list type="number">
///   <item><b>unhealthy</b> — the health-check roll-up is <c>Unhealthy</c> (a critical dependency such
///   as the database or Redis is down; the system is not fully serving).</item>
///   <item><b>degraded</b> — not unhealthy, but any of: the health roll-up is <c>Degraded</c>; at least
///   one <c>Critical</c> finding is active; a deploy is parked in ManualInterventionRequired; alert
///   deliveries have dead-lettered; the alert dispatcher is impaired; or a configured availability SLO
///   has exhausted its error budget.</item>
///   <item><b>healthy</b> — none of the above.</item>
/// </list>
/// Warning/Info findings and pending (non-parking) work do not by themselves downgrade the verdict —
/// they are surfaced in the domain rollups for an operator to weigh, keeping the verdict honest about
/// what actually threatens serving.
/// </remarks>
public static class OperateStatusVerdictEvaluator
{
    /// <summary>
    /// Computes the overall verdict and the machine-readable reasons that drove it.
    /// </summary>
    /// <param name="signals">The aggregated verdict signals.</param>
    /// <returns>The overall status and an ordered, deduplicated list of reason tokens.</returns>
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

        if (signals.SloErrorBudgetExhausted)
        {
            reasons.Add("slo-error-budget-exhausted");
        }

        var status = reasons.Count > 0 ? OperateOverallStatus.Degraded : OperateOverallStatus.Healthy;
        return (status, reasons);
    }

    /// <summary>Maps the verdict enum to its lowercase wire token.</summary>
    /// <param name="status">The verdict.</param>
    /// <returns>The wire string (<c>healthy</c>/<c>degraded</c>/<c>unhealthy</c>).</returns>
    public static string ToWire(OperateOverallStatus status) => status switch
    {
        OperateOverallStatus.Healthy => "healthy",
        OperateOverallStatus.Degraded => "degraded",
        OperateOverallStatus.Unhealthy => "unhealthy",
        _ => "unhealthy",
    };
}
