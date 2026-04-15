// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Validates analysis plan steps against the process catalog, checking that
/// referenced process IDs exist and required parameters are supplied.
/// </summary>
internal static class ProcessPlanValidator
{
    /// <summary>
    /// Validates all <see cref="AnalysisPlanStepKind.Geoprocess"/> steps in the plan
    /// against the catalog, returning any violations and warnings found.
    /// </summary>
    public static (List<GeoprocessingValidationFailure> Violations, List<string> Warnings) Validate(
        AnalysisPlan plan,
        IProcessCatalog catalog)
    {
        var violations = new List<GeoprocessingValidationFailure>();
        var warnings = new List<string>();

        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.ProcessId))
            {
                violations.Add(new GeoprocessingValidationFailure
                {
                    Code = "MISSING_PROCESS_ID",
                    Message = $"Geoprocess step '{step.StepId}' requires a process identifier.",
                    FieldPath = $"steps[{step.StepId}].process_id"
                });
                continue;
            }

            var definition = catalog.GetProcess(step.ProcessId);
            if (definition == null)
            {
                violations.Add(new GeoprocessingValidationFailure
                {
                    Code = "UNKNOWN_PROCESS",
                    Message = $"Process '{step.ProcessId}' referenced by step '{step.StepId}' is not in the catalog.",
                    FieldPath = $"steps[{step.StepId}].process_id"
                });
                continue;
            }

            foreach (var param in definition.Parameters)
            {
                if (!param.Required)
                {
                    continue;
                }

                if (!step.Inputs.ContainsKey(param.Name))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "MISSING_REQUIRED_PARAMETER",
                        Message = $"Step '{step.StepId}' is missing required parameter '{param.Name}' for process '{step.ProcessId}'.",
                        FieldPath = $"steps[{step.StepId}].inputs.{param.Name}"
                    });
                }
            }
        }

        return (violations, warnings);
    }
}
