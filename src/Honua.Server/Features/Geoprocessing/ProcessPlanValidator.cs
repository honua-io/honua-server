// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Validates analysis plan steps against the process catalog, checking that
/// referenced process IDs exist, required parameters are supplied, and supplied
/// values parse cleanly against their declared <see cref="ProcessParameterValueType"/>.
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

            var paramsByName = definition.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

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

            foreach (var (inputName, inputValue) in step.Inputs)
            {
                if (!paramsByName.TryGetValue(inputName, out var spec))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "UNKNOWN_PARAMETER",
                        Message = $"Step '{step.StepId}' supplies unknown parameter '{inputName}' for process '{step.ProcessId}'.",
                        FieldPath = $"steps[{step.StepId}].inputs.{inputName}"
                    });
                    continue;
                }

                if (!IsValidForType(inputValue, spec.ValueType, out var typeErrorDetail))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "INVALID_PARAMETER_VALUE",
                        Message = $"Step '{step.StepId}' supplies invalid value for parameter '{inputName}' of process '{step.ProcessId}': {typeErrorDetail}.",
                        FieldPath = $"steps[{step.StepId}].inputs.{inputName}"
                    });
                }
            }
        }

        return (violations, warnings);
    }

    private static bool IsValidForType(string? value, ProcessParameterValueType type, out string errorDetail)
    {
        errorDetail = "";

        if (value is null)
        {
            errorDetail = "value must not be null";
            return false;
        }

        switch (type)
        {
            case ProcessParameterValueType.Text:
                return true;

            case ProcessParameterValueType.WholeNumber:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    errorDetail = $"expected 32-bit integer, got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.FloatingPoint:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)
                    || double.IsNaN(dbl)
                    || double.IsInfinity(dbl))
                {
                    errorDetail = $"expected finite floating-point number, got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.Flag:
                if (!bool.TryParse(value, out _))
                {
                    errorDetail = $"expected boolean flag ('true' or 'false'), got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.Srid:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) || srid <= 0)
                {
                    errorDetail = $"expected positive SRID, got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.Wkb:
                if (!TryDecodeBase64NonEmpty(value))
                {
                    errorDetail = "expected base64-encoded WKB";
                    return false;
                }
                return true;

            case ProcessParameterValueType.WkbArray:
                if (!TryDecodeWkbArray(value, out var arrayError))
                {
                    errorDetail = arrayError;
                    return false;
                }
                return true;

            case ProcessParameterValueType.LayerId:
                if (string.IsNullOrWhiteSpace(value))
                {
                    errorDetail = "expected non-empty layer identifier";
                    return false;
                }
                return true;

            default:
                return true;
        }
    }

    private static bool TryDecodeBase64NonEmpty(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written > 0;
    }

    private static bool TryDecodeWkbArray(string value, out string errorDetail)
    {
        errorDetail = "";

        if (string.IsNullOrWhiteSpace(value))
        {
            errorDetail = "expected JSON array of base64-encoded WKB strings";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                errorDetail = "expected JSON array of base64-encoded WKB strings";
                return false;
            }

            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    errorDetail = $"WKB array element at index {index} is not a string";
                    return false;
                }

                var item = element.GetString();
                if (item is null || !TryDecodeBase64NonEmpty(item))
                {
                    errorDetail = $"WKB array element at index {index} is not a valid base64 WKB string";
                    return false;
                }

                index++;
            }

            if (index == 0)
            {
                errorDetail = "WKB array must contain at least one geometry";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            errorDetail = $"WKB array is not valid JSON: {ex.Message}";
            return false;
        }
    }
}
