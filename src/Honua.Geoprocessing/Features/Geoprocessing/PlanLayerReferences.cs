// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// A catalog layer a submitted plan will touch, together with the step and parameter
/// that named it. Carried so a denial can name the offending step without the caller
/// having to guess which of several layer inputs was refused.
/// </summary>
/// <param name="StepId">The plan step that references the layer.</param>
/// <param name="ProcessId">The catalog process the step executes.</param>
/// <param name="ParameterName">The declared parameter whose value is the layer id.</param>
/// <param name="LayerId">The referenced catalog layer id.</param>
/// <param name="Access">Whether the process reads the layer or writes into it.</param>
internal readonly record struct PlanLayerReference(
    string StepId,
    string ProcessId,
    string ParameterName,
    int LayerId,
    ProcessLayerAccess Access);

/// <summary>
/// Derives the catalog layers an <see cref="AnalysisPlan"/> will read, GENERICALLY, from
/// the process catalog rather than from a hard-coded list of layer-sourced processes
/// (honua-server#3046).
/// </summary>
/// <remarks>
/// <para>
/// A parameter counts as a layer reference when the process definition declares it as
/// <see cref="ProcessParameterValueType.LayerId"/>. Any process added later that declares a
/// layer parameter is therefore covered by the submit-time authorization gate the moment it
/// is added to the catalog — no per-executor opt-in and nothing to forget.
/// </para>
/// <para>
/// The parameter also declares HOW the process uses the layer
/// (<see cref="ProcessParameterSpec.LayerAccess"/>), which is carried on the reference so the
/// gate can require a read grant on sources and a write grant on destinations. Deriving the
/// operation from the value type alone made every layer parameter a read, refusing an import
/// whose caller held the mutating grant but was deliberately denied read on the destination
/// layer (honua-server#3046 review).
/// </para>
/// <para>
/// The value is read with a case-insensitive key match even though the worker-side
/// <c>StepInputReader</c> resolves the durable spec key with ordinal comparison. Matching more
/// keys than the executor reads can only ever authorize MORE layers than are actually read, so
/// a differently-cased input can never smuggle a read past the gate.
/// </para>
/// </remarks>
internal static class PlanLayerReferences
{
    /// <summary>
    /// Returns one entry per DISTINCT layer id the plan references, in plan order.
    /// Steps whose process is not in the catalog, parameters that are absent, and values
    /// that are not integers are skipped: none of them results in a catalog layer read
    /// (unknown processes and malformed inputs are rejected by plan validation and by the
    /// executors themselves).
    /// </summary>
    /// <param name="plan">The plan being submitted.</param>
    /// <param name="catalog">The process catalog that declares parameter value types.</param>
    /// <returns>The distinct layer references, in the order they appear in the plan.</returns>
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
}
