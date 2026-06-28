// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Classifies canonical process ids by whether they require the approval gate —
/// either because they mutate/erase caller-owned data (destructive) or because
/// they write data out to an external/owned destination (sink/write). Such plans
/// route through the existing approval gate (with
/// <c>OperatorAuthorizationRequest.IsDestructive = true</c>) before any job or
/// progress record is created: when
/// <c>Operator:Approval:DestructiveActionsRequireApproval</c> is on, submission
/// hard-fails with <see cref="GeoprocessingApprovalRequiredException"/>
/// (gRPC <c>FailedPrecondition</c>, OGC <c>403 Approval required</c>) instead of
/// persisting an <c>AwaitingApproval</c> progress entry. Pending-approval
/// persistence and <c>Validated → AwaitingApproval</c> status projection are
/// follow-on work — see ADR-0029.
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

    // Canonical ids that terminate a plan by WRITING the input FeatureCollection
    // out to an external or caller-owned destination. These produce durable
    // side-effects (rows in an external/catalog database, files on disk) and so
    // must pass the same approval gate as destructive mutations (#2262).
    // sink.quarantine is deliberately excluded: it is the internal dead-letter
    // half of the row-level-error contract (it only persists rows the pipeline
    // already rejected), not a caller-chosen data destination.
    private static readonly FrozenSet<string> SinkProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sink.external-postgis",
            "sink.honua-layer",
            "sink.geojson-file",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when the given process id represents a destructive
    /// layer-level mutation that must pass the approval gate.
    /// </summary>
    public static bool IsDestructive(string? processId)
        => !string.IsNullOrWhiteSpace(processId) && DestructiveProcessIds.Contains(processId);

    /// <summary>
    /// Returns true when the given process id writes the input out to an external
    /// or caller-owned destination (a sink/write process) and therefore must pass
    /// the approval gate.
    /// </summary>
    public static bool IsSink(string? processId)
        => !string.IsNullOrWhiteSpace(processId) && SinkProcessIds.Contains(processId);

    /// <summary>
    /// Returns true when the given process id must pass the approval gate, i.e.
    /// it is either destructive or a sink/write process.
    /// </summary>
    public static bool RequiresApproval(string? processId)
        => IsDestructive(processId) || IsSink(processId);

    /// <summary>
    /// Returns true when any <see cref="AnalysisPlanStepKind.Geoprocess"/> step
    /// in the plan references a destructive canonical process id.
    /// </summary>
    public static bool HasDestructiveStep(AnalysisPlan plan)
        => FindFirstDestructiveProcessId(plan) != null;

    /// <summary>
    /// Returns true when any <see cref="AnalysisPlanStepKind.Geoprocess"/> step
    /// in the plan references a process id that must pass the approval gate
    /// (destructive or sink/write).
    /// </summary>
    public static bool HasApprovalGatedStep(AnalysisPlan plan)
        => FindFirstApprovalGatedProcessId(plan) != null;

    /// <summary>
    /// Returns the first destructive canonical process id referenced by any
    /// <see cref="AnalysisPlanStepKind.Geoprocess"/> step, or <c>null</c> when
    /// the plan is non-destructive. Used for approval-gate telemetry so the
    /// emitted log carries the triggering process id.
    /// </summary>
    public static string? FindFirstDestructiveProcessId(AnalysisPlan plan)
        => FindFirst(plan, IsDestructive);

    /// <summary>
    /// Returns the first canonical process id referenced by any
    /// <see cref="AnalysisPlanStepKind.Geoprocess"/> step that must pass the
    /// approval gate (destructive or sink/write), or <c>null</c> when the plan
    /// needs no approval. Used to drive both the approval gate and its telemetry
    /// so a plan that writes to an external sink is gated identically to a
    /// destructive mutation (#2262).
    /// </summary>
    public static string? FindFirstApprovalGatedProcessId(AnalysisPlan plan)
        => FindFirst(plan, RequiresApproval);

    private static string? FindFirst(AnalysisPlan plan, Func<string?, bool> predicate)
    {
        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess)
            {
                continue;
            }

            if (predicate(step.ProcessId))
            {
                return step.ProcessId;
            }
        }

        return null;
    }
}
