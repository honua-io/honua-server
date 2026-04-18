// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Classifies canonical process ids by whether they mutate or erase caller-owned
/// data. Destructive plans route through the existing approval gate (with
/// <c>OperatorAuthorizationRequest.IsDestructive = true</c>) before
/// <c>GeoprocessingStageKind.Execute</c> so workflows transition
/// <c>Validated → AwaitingApproval</c> before any layer mutation runs.
///
/// Kept as a server-side static helper rather than a field on
/// <see cref="ProcessDefinition"/> so destruction policy stays policy-owned
/// and <c>Honua.Core</c> remains transport-neutral.
/// </summary>
internal static class ProcessDestructiveClassifier
{
    // Canonical ids that mutate or erase rows on the caller-owned source layer.
    // copy-features is deliberately excluded: it always materializes a new
    // target layer and does not modify the source.
    private static readonly FrozenSet<string> DestructiveProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "data-management.delete-features",
            "data-management.calculate-field",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when the given process id represents a destructive
    /// layer-level mutation that must pass the approval gate.
    /// </summary>
    public static bool IsDestructive(string? processId)
        => !string.IsNullOrWhiteSpace(processId) && DestructiveProcessIds.Contains(processId);

    /// <summary>
    /// Returns true when any <see cref="AnalysisPlanStepKind.Geoprocess"/> step
    /// in the plan references a destructive canonical process id.
    /// </summary>
    public static bool HasDestructiveStep(AnalysisPlan plan)
        => FindFirstDestructiveProcessId(plan) != null;

    /// <summary>
    /// Returns the first destructive canonical process id referenced by any
    /// <see cref="AnalysisPlanStepKind.Geoprocess"/> step, or <c>null</c> when
    /// the plan is non-destructive. Used for approval-gate telemetry so the
    /// emitted log carries the triggering process id.
    /// </summary>
    public static string? FindFirstDestructiveProcessId(AnalysisPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess)
            {
                continue;
            }

            if (IsDestructive(step.ProcessId))
            {
                return step.ProcessId;
            }
        }

        return null;
    }
}
