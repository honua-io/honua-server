// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal static class GpPlanner
{
    public static GpPlan? Build(
        string processId,
        IReadOnlyDictionary<string, string> resolvedInputs,
        IProcessCatalog catalog,
        long maxArtifactBytes,
        long callerInputBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(resolvedInputs);
        ArgumentNullException.ThrowIfNull(catalog);

        var definition = catalog.GetProcess(processId);
        if (definition is null)
        {
            return null;
        }

        // A single-process plan: the GP Devkit CLI drives ONE process at a time,
        // but we still build a real AnalysisPlan and run the shared structural
        // validation so a future multi-step authoring path reuses the same gate.
        var plan = new AnalysisPlan
        {
            PlanId = $"gp-plan-{processId}",
            IntentId = "gp-cli",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-0",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = processId,
                    Inputs = new Dictionary<string, string>(resolvedInputs, StringComparer.Ordinal),
                    DependsOn = [],
                },
            ],
            Outputs = definition.OutputArtifactKinds,
        };

        var errors = new List<string>();

        // Structural / DAG validation — the same Kahn topological check the durable
        // submit path runs. Surfaces dangling deps, self-deps, and cycles.
        try
        {
            AnalysisPlanGraphValidator.Validate(plan);
        }
        catch (GeoprocessingValidationException ex)
        {
            errors.Add(ex.Message);
        }

        // Typed-schema parameter validation — the same ProcessPlanValidator the
        // durable validate/submit path runs (required/unknown params, per-type
        // parsing, per-process semantic rules).
        var (violations, validatorWarnings) = ProcessPlanValidator.Validate(plan, catalog);
        foreach (var violation in violations)
        {
            errors.Add($"{violation.Code}: {violation.Message}");
        }

        var warnings = new List<string>(validatorWarnings);

        var steps = new[]
        {
            new GpPlanStep
            {
                StepId = plan.Steps[0].StepId,
                ProcessId = processId,
                DependsOn = plan.Steps[0].DependsOn,
                Parameters = ResolveParameters(definition, resolvedInputs),
            },
        };

        var estimate = GpSizeEstimator.Estimate(definition, callerInputBytes, maxArtifactBytes);
        AppendEstimateWarnings(estimate, warnings);

        return new GpPlan
        {
            ProcessId = processId,
            Title = definition.Title,
            Category = definition.Category,
            RuntimeProfile = RuntimeProfiles.Normalize(definition.RuntimeProfile),
            Errors = errors,
            Warnings = warnings,
            Steps = steps,
            Outputs = definition.OutputArtifactKinds,
            Estimate = estimate,
        };
    }

    private static GpResolvedParam[] ResolveParameters(
        ProcessDefinition definition,
        IReadOnlyDictionary<string, string> resolvedInputs)
    {
        var resolved = new List<GpResolvedParam>(definition.Parameters.Count);
        foreach (var spec in definition.Parameters)
        {
            string? displayValue;
            GpParamSource source;

            if (resolvedInputs.TryGetValue(spec.Name, out var supplied))
            {
                displayValue = Abbreviate(supplied, spec.ValueType);
                source = GpParamSource.Caller;
            }
            else if (spec.DefaultValue is not null)
            {
                displayValue = Abbreviate(spec.DefaultValue, spec.ValueType);
                source = GpParamSource.Default;
            }
            else
            {
                displayValue = null;
                source = GpParamSource.Unset;
            }

            resolved.Add(new GpResolvedParam
            {
                Name = spec.Name,
                ValueType = spec.ValueType,
                Required = spec.Required,
                DisplayValue = displayValue,
                Source = source,
            });
        }

        return resolved.ToArray();
    }

    private static void AppendEstimateWarnings(GpSizeEstimate estimate, List<string> warnings)
    {
        if (estimate.ExceedsCap && estimate.EstimatedOutputBytes is { } outputBytes)
        {
            warnings.Add(
                $"Estimated output (~{FormatBytes(outputBytes)}) is likely to exceed the " +
                $"{FormatBytes(estimate.MaxArtifactBytes)} MaxArtifactBytes cap — the job FAILS at that cap today. " +
                "This is a heuristic estimate, not a guarantee; reduce the input or raise " +
                "Geoprocessing:Executors:MaxArtifactBytes before submitting.");
        }
        else if (estimate.InputBytes > estimate.MaxArtifactBytes)
        {
            // Even when the output estimate stays under the cap, an input already
            // larger than the cap is a strong signal the run is at risk.
            warnings.Add(
                $"Input payload (~{FormatBytes(estimate.InputBytes)}) already exceeds the " +
                $"{FormatBytes(estimate.MaxArtifactBytes)} MaxArtifactBytes cap; the output is likely to as well.");
        }

        if (estimate.LongRunning)
        {
            warnings.Add(
                "This operation is raster/surface-class and may be long-running and cost-sensitive " +
                "over a large or multi-tile input; budget compute accordingly.");
        }
    }

    private static string Abbreviate(string value, ProcessParameterValueType valueType)
    {
        var isBinary = valueType is ProcessParameterValueType.Wkb or ProcessParameterValueType.WkbArray;
        if (isBinary || value.Length > 64)
        {
            return $"<{value.Length} chars>";
        }

        return value;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var unitIndex = -1;
        do
        {
            value /= 1024d;
            unitIndex++;
        }
        while (value >= 1024d && unitIndex < units.Length - 1);

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unitIndex]}");
    }
}
