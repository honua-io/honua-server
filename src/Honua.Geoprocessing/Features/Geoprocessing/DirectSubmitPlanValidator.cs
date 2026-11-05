// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal static class DirectSubmitPlanValidator
{
    public static (List<GeoprocessingValidationFailure> Violations, List<string> Warnings) Evaluate(
        AnalysisPlan plan,
        IProcessCatalog processCatalog)
    {
        ArgumentNullException.ThrowIfNull(processCatalog);

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
                    && processCatalog.GetProcess(step.ProcessId) is { } definition
                    && !ProcessExecutionEligibility.IsJobCallable(definition))
                {
                    var code = definition.ExecutionKind switch
                    {
                        ProcessExecutionKind.Job => "ASYNC_EXECUTION_UNSUPPORTED",
                        ProcessExecutionKind.ProtocolOnly => "SYNC_ONLY_PROCESS",
                        ProcessExecutionKind.WorkflowOnly when
                            ProcessExecutionEligibility.IsWorkflowCallable(definition) => "WORKFLOW_ONLY_PROCESS",
                        ProcessExecutionKind.WorkflowOnly => "ASYNC_EXECUTION_UNSUPPORTED",
                        ProcessExecutionKind.Unavailable => "PROCESS_UNAVAILABLE",
                        _ => "UNCLASSIFIED_PROCESS"
                    };
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = code,
                        Message = $"Process '{step.ProcessId}' referenced by step '{step.StepId}' is classified "
                            + $"as {definition.ExecutionKind} with modes '{definition.SupportedExecutionModes}' "
                            + "and cannot be submitted as an asynchronous process job. "
                            + (definition.ExecutionCapabilityReason ?? "The catalog does not declare a job execution capability."),
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
