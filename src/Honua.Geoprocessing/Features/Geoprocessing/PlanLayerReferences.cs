// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Geoprocessing;

internal readonly record struct PlanLayerReference(
    string StepId,
    string ProcessId,
    string ParameterName,
    int LayerId,
    ProcessLayerAccess Access);

// The parameter declares read or write; treating every layer parameter as a read refused imports the caller was allowed to write.
internal static class PlanLayerReferences
{
    public static IReadOnlyList<PlanLayerReference> Derive(AnalysisPlan plan, IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);

        List<PlanLayerReference>? references = null;
        HashSet<(int LayerId, ProcessLayerAccess Access)>? seen = null;

        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess || string.IsNullOrWhiteSpace(step.ProcessId))
            {
                continue;
            }

            var definition = catalog.GetProcess(step.ProcessId);
            if (definition is null || step.Inputs.Count == 0)
            {
                continue;
            }

            var usesInlineRasterSource = GeoprocessingRasterSourceResolution.UsesInlineSource(step, definition);

            foreach (var parameter in definition.Parameters)
            {
                // ProcessLayerAccess.None marks a reserved placeholder the executor never
                // resolves, so there is no read to authorize — gating it denied jobs for data
                // the process never touches (honua-server#3046 review).
                if (parameter.ValueType != ProcessParameterValueType.LayerId
                    || parameter.LayerAccess == ProcessLayerAccess.None)
                {
                    continue;
                }

                // Native raster selectors are alternatives. When an inline source is present,
                // resolution and the worker both ignore layerId/rasterId, so authorizing the
                // unused layer would turn a harmless stale selector into a false 403.
                if (usesInlineRasterSource
                    && string.Equals(parameter.Name, "layerId", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var input in step.Inputs)
                {
                    if (!string.Equals(input.Key, parameter.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!int.TryParse(input.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
                    {
                        continue;
                    }

                    if (!IsActiveLayerReference(step, parameter, layerId))
                    {
                        continue;
                    }

                    seen ??= [];
                    if (!seen.Add((layerId, parameter.LayerAccess)))
                    {
                        continue;
                    }

                    references ??= [];
                    references.Add(new PlanLayerReference(
                        step.StepId,
                        step.ProcessId!,
                        parameter.Name,
                        layerId,
                        parameter.LayerAccess));
                }
            }
        }

        return references is null ? [] : references;
    }

    private static bool IsActiveLayerReference(
        AnalysisPlanStep step,
        ProcessParameterSpec parameter,
        int layerId)
    {
        if (!string.Equals(step.ProcessId, "import.dataset", StringComparison.Ordinal)
            || !string.Equals(parameter.Name, "rasterLayerId", StringComparison.Ordinal))
        {
            return true;
        }

        return layerId > 0
            && step.Inputs.TryGetValue("fileName", out var fileName)
            && RasterImportFormatDetector.DetectFormat(fileName) is not null;
    }
}
