// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

// Destruction policy stays here rather than on ProcessDefinition so Honua.Core remains transport-neutral.
internal static class ProcessDestructiveClassifier
{
    // Known-safe MUTATING processes: they carry the Mutating execution tier (so the
    // fail-closed rule below would otherwise gate them) but are deliberately NOT
    // approval-gated. copy-features always materializes a NEW target layer and never
    // modifies the source; sink.quarantine is the internal dead-letter half of the
    // row-level-error contract (it only persists rows the pipeline already rejected),
    // written only to a new server-owned job/attempt namespace. A requested name
    // cannot select another job's destination. Any other mutating process is gated.
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
    // already rejected) in its own immutable job/attempt output namespace.
    private static readonly FrozenSet<string> SinkProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sink.external-postgis",
            "sink.honua-layer",
            "sink.geojson-file",
        }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsDestructive(string? processId)
        => !string.IsNullOrWhiteSpace(processId) && DestructiveProcessIds.Contains(processId);

    public static bool IsSink(string? processId)
        => !string.IsNullOrWhiteSpace(processId) && SinkProcessIds.Contains(processId);

    public static bool RequiresApproval(string? processId)
        => IsDestructive(processId) || IsSink(processId);

    public static bool HasDestructiveStep(AnalysisPlan plan)
        => FindFirstDestructiveProcessId(plan) != null;

    public static bool HasApprovalGatedStep(AnalysisPlan plan)
        => FindFirstApprovalGatedProcessId(plan) != null;

    public static string? FindFirstDestructiveProcessId(AnalysisPlan plan)
        => FindFirst(plan, IsDestructive);

    public static string? FindFirstApprovalGatedProcessId(AnalysisPlan plan)
        => FindFirst(plan, RequiresApproval);

    // An unknown process is approval-gated so a new mutating process cannot skip the gate.
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

    public static string? FindFirstApprovalGatedProcessId(AnalysisPlan plan, IProcessCatalog catalog)
        => FindFirst(plan, id => RequiresApproval(id, catalog));

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
