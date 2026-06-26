// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;

namespace Honua.Geoprocessing;

/// <summary>
/// Single source of truth for projecting an <see cref="AnalysisPlan"/> onto the
/// durable <see cref="ExecutionJobSpec.Parameters"/> bag the worker reads back.
/// Both the production submit path (<c>GeoprocessingJobService.BuildSpec</c>) and
/// the GP Devkit local runner (<c>GeoprocessingLocalRunner</c>) build their spec
/// parameters through this one helper so the spec the local dry-run carries is the
/// IDENTICAL spec a real submit would produce for the same plan — closing the
/// "plan is a parallel representation" gap flagged by issue #2180.
/// </summary>
internal static class GeoprocessingSpecBuilder
{
    /// <summary>
    /// Projects <paramref name="plan"/>'s id, process-definitions list, output-artifact
    /// kinds, and per-step inputs into <paramref name="specParams"/> under the canonical
    /// <c>ExecutionJobParameterKeys</c> keys. Mutates and returns the supplied bag so the
    /// submit path can layer admission/partition/workload parameters around this core
    /// projection. The local runner passes a fresh bag and gets exactly the durable
    /// parameters a worker dispatches on.
    /// </summary>
    public static Dictionary<string, string> ProjectPlanParameters(
        AnalysisPlan plan,
        Dictionary<string, string> specParams)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(specParams);

        specParams[ExecutionJobParameterKeys.GeoprocessingPlanId] = plan.PlanId;

        var processDefinitions = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.ProcessId))
            .Select(step => step.ProcessId!)
            .ToArray();
        if (processDefinitions.Length > 0)
        {
            specParams[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = string.Join(
                ExecutionJobParameterKeys.MetadataListSeparator,
                processDefinitions);
        }

        var outputKinds = plan.Outputs.Select(output => output.ToString()).ToArray();
        if (outputKinds.Length > 0)
        {
            specParams[ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = string.Join(
                ExecutionJobParameterKeys.MetadataListSeparator,
                outputKinds);
        }

        // Project plan step inputs onto the durable spec under a stable prefix so
        // worker-side executors can read their parameters without reaching back into
        // the analysis plan. Only `Geoprocess` steps carry semantic inputs in the
        // first-slice catalog; other kinds are ignored here.
        for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            if (step.Kind != AnalysisPlanStepKind.Geoprocess || step.Inputs.Count == 0)
            {
                continue;
            }

            foreach (var input in step.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Key))
                {
                    continue;
                }

                var key = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}{stepIndex}.{input.Key}";
                specParams[key] = input.Value ?? string.Empty;
            }
        }

        return specParams;
    }

    /// <summary>
    /// Builds the <see cref="ExecutionJobSpec"/> for the no-registered-workload case —
    /// the shape both the production submit path (when no
    /// <c>ExecutionJobDefinition</c> is registered for the geoprocessing kind, the
    /// default) and the GP Devkit local runner produce. Centralizing it here makes the
    /// local dry-run's spec IDENTICAL to a real submit's by construction: same Kind /
    /// TargetKind / Backend / WorkloadName / RuntimeProfile, and the same parameter bag
    /// from <see cref="ProjectPlanParameters"/>.
    /// </summary>
    /// <param name="plan">The analysis plan to project.</param>
    /// <param name="specParams">
    /// The parameter bag to project into (the submit path seeds protocol/admission
    /// metadata first; the local runner passes a fresh bag).
    /// </param>
    /// <param name="requiredRuntimeProfile">
    /// The data-driven runtime profile (<c>native</c> for a gdal.* step; <c>null</c>
    /// leaves the job managed/default). Drives the claim fence to the right worker.
    /// </param>
    public static ExecutionJobSpec BuildNoWorkloadSpec(
        AnalysisPlan plan,
        Dictionary<string, string> specParams,
        string? requiredRuntimeProfile)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(specParams);

        ProjectPlanParameters(plan, specParams);

        return new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = LocalBatchComputeBackend.BackendId,
            WorkloadName = $"geoprocessing:{plan.PlanId}",
            // Data-driven native-profile stamping: a catalog process that requires a
            // specialized worker (the gdal.* native family) forces this profile so the
            // claim fence routes the job to the GDAL worker and away from the lean
            // dispatcher. Null leaves the job managed/default.
            RuntimeProfile = requiredRuntimeProfile,
            Parameters = specParams,
        };
    }

    /// <summary>
    /// Builds a single-step <see cref="AnalysisPlan"/> for a direct
    /// <c>(processId, step-0 inputs)</c> invocation — the shape the GP Devkit local
    /// runner / <c>honua gp run</c> drive. The plan is the canonical input the shared
    /// projection and spec-shape consume, so the local dry-run produces the same spec
    /// a single-step submit would.
    /// </summary>
    /// <param name="processId">Canonical dotted process id (e.g. <c>gdal.hillshade</c>).</param>
    /// <param name="inputs">Step-0 input name/value pairs.</param>
    /// <param name="planId">Stable plan id to stamp; the runner supplies its operation id.</param>
    public static AnalysisPlan SingleStepPlan(
        string processId,
        IReadOnlyDictionary<string, string> inputs,
        string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        var stepInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in inputs)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            stepInputs[name] = value ?? string.Empty;
        }

        return new AnalysisPlan
        {
            PlanId = planId,
            IntentId = planId,
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "0",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = stepInputs,
                },
            ],
        };
    }
}
