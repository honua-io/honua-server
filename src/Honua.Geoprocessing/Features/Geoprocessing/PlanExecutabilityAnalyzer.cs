// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Produces honest advisories about how the direct single-step geoprocessing execution path
/// will run an <see cref="AnalysisPlan"/>, versus how the plan reads as authored (#2806).
/// </summary>
/// <remarks>
/// <para>
/// The direct submit/dispatch path (MCP <c>honua_execute_plan</c>, gRPC <c>SubmitJob</c>,
/// OGC API Processes execution) is a single-process runtime: the dispatcher resolves exactly
/// ONE process id (the first step that carries a <see cref="AnalysisPlanStep.ProcessId"/>) and
/// every per-process executor reads its inputs from the fixed <c>step.0.</c> parameter prefix.
/// A plan that reads as a multi-step DAG therefore does NOT run as authored on that path:
/// steps beyond the first process step are silently dropped, a process step that is not at
/// position 0 fails because its inputs are projected under <c>step.{index}.</c> where the
/// executor never looks, and a non-Geoprocess step is ignored entirely.
/// </para>
/// <para>
/// Every such plan is still a valid plan in the broader system — a multi-step DAG is
/// decomposed and run by the workflow orchestration engine, and a synchronous-only
/// (non-job-dispatchable) catalog process still runs through its own protocol surface — so the
/// canonical <c>ValidatePlan</c> keeps reporting it as <c>isExecutable</c>=true (and, for a
/// destructive process, approval-gated). What was missing, and what this analyzer supplies, is
/// a clear, structured warning on the validate/dry-run surfaces so an agent is never SILENTLY
/// told a hand-authored multi-step or sync-only plan will run as written on the direct path.
/// </para>
/// <para>
/// This analyzer is the single source of truth for those advisories so the read-only
/// <c>honua_validate_plan</c>/<c>honua_dry_run_plan</c> MCP surfaces and the gRPC/GPServer/OGC
/// validate adapters all describe the same execution reality rather than each re-deriving it.
/// It intentionally does NOT implement multi-step DAG execution; it makes the plan contract
/// honest about what a single direct-submit job runs.
/// </para>
/// </remarks>
internal static class PlanExecutabilityAnalyzer
{
    /// <summary>
    /// Returns the direct-execution-path advisories for <paramref name="plan"/>: warnings that
    /// the single-step runtime will drop, misplace, ignore, or be unable to dispatch parts of
    /// the plan. An empty list means the plan runs on the direct path exactly as authored.
    /// </summary>
    /// <param name="plan">The plan to analyze.</param>
    /// <param name="isProcessDispatchable">
    /// Predicate that returns <c>true</c> when a process id can be dispatched as a job (a
    /// managed process with a registered executor, or a native-profile process served by the
    /// out-of-process worker). Pass <c>null</c> to skip the sync-only advisory when the
    /// dispatchable set is not known to the caller.
    /// </param>
    /// <returns>The ordered advisory messages; empty when the plan is a faithful single step.</returns>
    public static IReadOnlyList<string> AnalyzeDirectExecution(
        AnalysisPlan plan,
        Func<string, bool>? isProcessDispatchable)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var warnings = new List<string>();
        var steps = plan.Steps;

        // Steps that carry a process id, in plan order. The dispatcher resolves the FIRST of
        // these as the single process the direct-submit job runs.
        var processStepIndexes = new List<int>();
        for (var i = 0; i < steps.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(steps[i].ProcessId))
            {
                processStepIndexes.Add(i);
            }
        }

        if (processStepIndexes.Count > 1)
        {
            var firstId = steps[processStepIndexes[0]].ProcessId;
            var droppedIds = processStepIndexes
                .Skip(1)
                .Select(i => steps[i].ProcessId)
                .ToList();

            warnings.Add(
                $"The direct execution path (honua_execute_plan / SubmitJob) runs a single process — the " +
                $"first process step, '{firstId}'; the remaining {droppedIds.Count} process step(s) " +
                $"[{string.Join(", ", droppedIds)}] would be silently dropped. Submit one process per job, or " +
                "run this multi-step DAG through the workflow orchestration engine.");
        }

        if (processStepIndexes.Count > 0 && processStepIndexes[0] > 0)
        {
            var index = processStepIndexes[0];
            var step = steps[index];

            warnings.Add(
                $"Process step '{step.StepId}' ('{step.ProcessId}') is at position {index}, but the direct " +
                "execution path reads process inputs from position 0; the preceding step(s) shift its inputs " +
                "out of range and direct execution fails with missing inputs. Make the process step the first " +
                "step, or run the plan through the workflow orchestration engine.");
        }

        if (isProcessDispatchable is not null)
        {
            foreach (var index in processStepIndexes)
            {
                var processId = steps[index].ProcessId!;
                if (!isProcessDispatchable(processId))
                {
                    warnings.Add(
                        $"Process '{processId}' is a synchronous-only catalog process with no job executor, so " +
                        "the direct execution path (honua_execute_plan / SubmitJob) would queue a job that then " +
                        "fails at runtime. Use a job-dispatchable process (for example its layer-sourced or " +
                        "'-managed' variant) or invoke it through its synchronous protocol surface.");
                }
            }
        }

        // Non-Geoprocess steps carry no process id and are not executed by the single-step
        // runtime, so warn that they are ignored rather than letting an author believe the
        // schema's QueryFeatures/Aggregate/RenderMap/Export kinds run on the direct path.
        foreach (var step in steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess)
            {
                warnings.Add(
                    $"Step '{step.StepId}' (kind {step.Kind}) is not executed by the single-step geoprocessing " +
                    "runtime and will be ignored on the direct execution path; only a single Geoprocess step at " +
                    "position 0 is executed.");
            }
        }

        return warnings;
    }
}
