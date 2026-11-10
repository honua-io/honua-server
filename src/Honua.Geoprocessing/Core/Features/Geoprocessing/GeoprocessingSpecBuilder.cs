// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.ControlPlane;

namespace Honua.Geoprocessing;

internal static class GeoprocessingSpecBuilder
{
    public static Dictionary<string, string> ProjectPlanParameters(
        AnalysisPlan plan,
        Dictionary<string, string> specParams)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(specParams);

        var planValidation = RasterSourcePlanValidator.Validate(plan);
        if (!planValidation.IsValid)
        {
            var failure = planValidation.Errors[0];
            throw new ArgumentException(
                $"Raster source plan is invalid ({failure.Code}): {failure.Message}",
                nameof(plan));
        }

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
            if (step.Kind != AnalysisPlanStepKind.Geoprocess)
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

            foreach (var source in step.RasterSources)
            {
                if (string.IsNullOrWhiteSpace(source.Key))
                {
                    throw new ArgumentException(
                        $"Raster source parameter names cannot be empty on step '{step.StepId}'.",
                        nameof(plan));
                }

                var validation = RasterSourceDescriptorValidator.Validate(source.Value);
                if (!validation.IsValid)
                {
                    var failure = validation.Errors[0];
                    throw new ArgumentException(
                        $"Raster source '{source.Key}' on step '{step.StepId}' is invalid "
                        + $"({failure.Code}): {failure.Message}",
                        nameof(plan));
                }

                var key = $"{ExecutionJobParameterKeys.GeoprocessingStepRasterSourcePrefix}{stepIndex}.{source.Key}";
                specParams[key] = RasterSourceJson.Serialize(source.Value);
            }
        }

        return specParams;
    }

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
            ContractVersion = ResolveRequiredContractVersion(plan),
            Parameters = specParams,
        };
    }

    public static int ResolveRequiredContractVersion(AnalysisPlan plan, int baselineVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Steps.Any(step => step.RasterSources.Count > 0)
            ? Math.Max(baselineVersion, RasterSourceContract.JobContractVersion)
            : baselineVersion;
    }

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
