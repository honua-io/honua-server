// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Surfaces the direct-submit execution reality of an analysis plan so the
/// read-only pre-flight surfaces (<c>honua_validate_plan</c> / <c>honua_dry_run_plan</c>,
/// gRPC and GPServer dry-run) report the same limitations the submit path enforces
/// in <c>GeoprocessingJobService.EnsurePlanExecutable</c> and the per-process
/// dispatcher applies at runtime.
/// </summary>
/// <remarks>
/// A geoprocessing job runs exactly ONE process: the dispatcher resolves a single
/// process id from the spec (<c>GeoprocessingDispatchHelper.ResolveProcessId</c> takes
/// the first id) and the executors read their inputs from the canonical first-step
/// (<c>0.</c>) prefix. So a plan authored as a multi-step DAG, or one whose only
/// executable step is a non-<see cref="AnalysisPlanStepKind.Geoprocess"/> kind, or one
/// referencing a process that is not job-dispatchable, is accepted by the parameter-shape
/// catalog validator yet fails (or silently under-executes) at submit/runtime. This
/// validator translates those realities into structured violations/warnings so the
/// pre-flight is honest rather than optimistic (#2806).
/// </remarks>
internal static class DirectSubmitPlanValidator
{
    /// <summary>
    /// Catalog process ids that are advertised for planning but run ONLY synchronously
    /// through the layer-scoped PostGIS <c>SpatialAnalytics</c> protocol — they have no
    /// job-dispatchable executor, so a submitted plan referencing them fails at dispatch
    /// with "unsupported process id". Their job-executable counterparts carry the
    /// <c>-managed</c> suffix (e.g. <c>analytics.cluster-managed</c>). Kept as a small,
    /// documented set here rather than reaching into the worker-side executor registry,
    /// which the lean serving image that runs validation does not host.
    /// </summary>
    internal static readonly FrozenSet<string> SyncOnlyProcessIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "analytics.cluster",
            "analytics.density",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Evaluates <paramref name="plan"/> against the direct-submit execution model and
    /// returns any blocking violations plus non-fatal warnings. Violations here mirror
    /// the submit-path guards (multi-step, sync-only, nothing-executable); warnings flag
    /// steps that the runtime silently ignores so a caller understands what will and will
    /// not run without having to submit and inspect the result.
    /// </summary>
    public static (List<GeoprocessingValidationFailure> Violations, List<string> Warnings) Evaluate(
        AnalysisPlan plan)
    {
        var violations = new List<GeoprocessingValidationFailure>();
        var warnings = new List<string>();

        if (plan.Steps.Count == 0)
        {
            return (violations, warnings);
        }

        var geoprocessStepCount = 0;
        var firstGeoprocessIndex = -1;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (step.Kind == AnalysisPlanStepKind.Geoprocess)
            {
                geoprocessStepCount++;
                if (firstGeoprocessIndex < 0)
                {
                    firstGeoprocessIndex = i;
                }

                if (!string.IsNullOrWhiteSpace(step.ProcessId)
                    && SyncOnlyProcessIds.Contains(step.ProcessId))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "SYNC_ONLY_PROCESS",
                        Message = $"Process '{step.ProcessId}' referenced by step '{step.StepId}' is not "
                            + "job-dispatchable: it runs only synchronously through the layer-scoped analytics "
                            + $"endpoints. Use its job-executable counterpart '{step.ProcessId}-managed' to run it as a plan.",
                        FieldPath = $"steps[{step.StepId}].process_id"
                    });
                }
            }
            else
            {
                // The dispatcher only executes a Geoprocess step (it resolves a single
                // process id); QueryFeatures/Aggregate/RenderMap/Export steps advertised
                // by the plan schema are silently ignored on the direct-submit path.
                warnings.Add($"Step '{step.StepId}' has kind '{step.Kind}', which the direct-submit "
                    + "geoprocessing runtime does not execute — only Geoprocess steps run. This step will be "
                    + "ignored; compose non-Geoprocess stages through the workflow orchestration engine instead.");
            }
        }

        if (geoprocessStepCount == 0)
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "NO_EXECUTABLE_STEP",
                Message = "Plan contains no Geoprocess step, so the direct-submit runtime has nothing to "
                    + "execute — every other step kind is ignored on this path.",
                FieldPath = "steps"
            });
        }

        // A single execution job runs exactly one process, so a multi-step plan submitted
        // directly would silently execute only the first process and drop the rest. Mirror
        // GeoprocessingJobService.EnsurePlanExecutable so validate/dry-run report the same
        // limitation the submit path enforces.
        if (plan.Steps.Count > 1)
        {
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "MULTI_STEP_NOT_EXECUTABLE",
                Message = "Multi-step plans are not executable on the direct-submit path: a job runs a single "
                    + "process, so only the first Geoprocess step would run and later steps would be silently "
                    + "dropped. Submit one process per job, or use the workflow orchestration engine to execute "
                    + "a multi-step DAG.",
                FieldPath = "steps"
            });
        }
        else if (firstGeoprocessIndex > 0)
        {
            // Single-step-count plans cannot reach here (index 0), but guard the general
            // case: a Geoprocess step preceded by ignored steps still only runs itself.
            violations.Add(new GeoprocessingValidationFailure
            {
                Code = "GEOPROCESS_STEP_NOT_FIRST",
                Message = "The only Geoprocess step is not first in the plan; the direct-submit runtime reads "
                    + "inputs from the first step, so preceding non-Geoprocess steps are ignored. Author the "
                    + "Geoprocess step as the single/first step, or use the workflow orchestration engine.",
                FieldPath = "steps"
            });
        }

        return (violations, warnings);
    }
}
