// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Abstractions;
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
/// (gRPC <c>FailedPrecondition</c>, OGC <c>403 Approval required</c>).
///
/// <para>
/// The catalog-aware <see cref="RequiresApproval(string?, IProcessCatalog)"/> /
/// <see cref="FindFirstApprovalGatedProcessId(AnalysisPlan, IProcessCatalog)"/>
/// overloads are <b>FAIL-CLOSED</b> (ADR-0029, #2814): rather than a hand-curated
/// destructive denylist, they gate every process whose
/// <see cref="ProcessDefinition.ExecutionTier"/> is
/// <see cref="ProcessExecutionTier.Mutating"/> AND every process the catalog does
/// not recognize at all (an unknown process is treated as approval-gated), with a
/// narrow explicit SAFE allowlist for the deliberately non-destructive mutating
/// processes (<c>copy-features</c> materializes a new target without touching the
/// source; <c>sink.quarantine</c> is the internal dead-letter half of the
/// row-error contract). The submit/validate path threads the process catalog into
/// these overloads so a newly-added mutating process is gated by default rather
/// than silently slipping through until someone remembers to extend a denylist.
/// The legacy string-only <see cref="IsDestructive"/>/<see cref="IsSink"/>
/// helpers remain a fixed known-destructive/known-sink set for the narrow callers
/// (grounding ambiguity, migration evidence) that have only a process id and no
/// catalog handle.
/// </para>
///
/// Kept as a server-side static helper rather than a field on
/// <see cref="ProcessDefinition"/> so destruction policy stays policy-owned
/// and <c>Honua.Core</c> remains transport-neutral.
/// </summary>
internal static class ProcessDestructiveClassifier
{
    // Known-safe MUTATING processes: they carry the Mutating execution tier (so the
    // fail-closed rule below would otherwise gate them) but are deliberately NOT
    // approval-gated. copy-features always materializes a NEW target layer and never
    // modifies the source; sink.quarantine is the internal dead-letter half of the
    // row-level-error contract (it only persists rows the pipeline already rejected),
    // not a caller-chosen data destination. Any other mutating process is gated.
    private static readonly FrozenSet<string> SafeMutatingProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "data-management.copy-features",
            "sink.quarantine",
        }.ToFrozenSet(StringComparer.Ordinal);

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

    /// <summary>
    /// FAIL-CLOSED approval gate for a single process id, derived from process
    /// metadata rather than a hand-curated denylist (ADR-0029, #2814). Returns
    /// <see langword="true"/> when the process:
    /// <list type="bullet">
    /// <item>writes to an external/caller-owned sink (<see cref="IsSink"/>), OR</item>
    /// <item>is unknown to <paramref name="catalog"/> (an unrecognized process is
    /// gated by default so a typo or newly-registered process cannot bypass the
    /// gate), OR</item>
    /// <item>declares <see cref="ProcessExecutionTier.Mutating"/> and is not on the
    /// narrow SAFE allowlist.</item>
    /// </list>
    /// Read-only/analytic processes known to the catalog return
    /// <see langword="false"/>. A blank id (a non-geoprocess step) is never gated.
    /// </summary>
    public static bool RequiresApproval(string? processId, IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(processId))
        {
            return false;
        }

        if (IsSink(processId))
        {
            return true;
        }

        if (SafeMutatingProcessIds.Contains(processId))
        {
            return false;
        }

        var definition = catalog.GetProcess(processId);
        if (definition is null)
        {
            // Fail closed: an unknown process must not silently execute unattended.
            return true;
        }

        return definition.ExecutionTier == ProcessExecutionTier.Mutating;
    }

    /// <summary>
    /// Returns the first canonical process id referenced by any
    /// <see cref="AnalysisPlanStepKind.Geoprocess"/> step that must pass the
    /// approval gate under the FAIL-CLOSED, catalog-driven policy, or
    /// <see langword="null"/> when the plan needs no approval. This is the overload
    /// the submit/validate path uses so a mutating or unknown process is gated by
    /// default (ADR-0029, #2814).
    /// </summary>
    public static string? FindFirstApprovalGatedProcessId(AnalysisPlan plan, IProcessCatalog catalog)
        => FindFirst(plan, id => RequiresApproval(id, catalog));

    /// <summary>
    /// Returns <see langword="true"/> when any
    /// <see cref="AnalysisPlanStepKind.Geoprocess"/> step in the plan must pass the
    /// approval gate under the FAIL-CLOSED, catalog-driven policy.
    /// </summary>
    public static bool HasApprovalGatedStep(AnalysisPlan plan, IProcessCatalog catalog)
        => FindFirstApprovalGatedProcessId(plan, catalog) != null;

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
