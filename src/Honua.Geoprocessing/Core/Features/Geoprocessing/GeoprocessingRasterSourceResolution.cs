// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Geoprocessing;

internal static class GeoprocessingRasterSourceResolution
{
    private const string SourceInput = "source";
    private const string LayerIdInput = "layerId";
    private const string RasterIdInput = "rasterId";

    public static async Task<AnalysisPlan> BindLayerIdsAsync(
        AnalysisPlan plan,
        IProcessCatalog catalog,
        IGeoprocessingRasterSourceResolver? resolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);

        List<AnalysisPlanStep>? rewritten = null;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (!TryGetReference(step, catalog, out var reference) || reference.RasterId is null)
            {
                continue;
            }

            if (resolver is null)
            {
                throw new GeoprocessingValidationException(
                    $"Step '{step.StepId}' references a catalog raster by rasterId, but layer-resolved "
                    + "raster sourcing is not available in this deployment (no raster catalog is configured). "
                    + "Supply an inline base64 'source' raster instead.");
            }

            var resolution = await resolver
                .ResolveLayerIdAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            if (!resolution.Found || resolution.LayerId is not { } layerId)
            {
                throw new GeoprocessingAuthorizationException(
                    requiresAuthentication: false,
                    "You do not have permission to read the referenced catalog raster.",
                    OperatorResourceType.Catalog,
                    OperatorOperation.Read,
                    AuthorizationDenialReason.InsufficientGrant);
            }

            rewritten ??= [.. plan.Steps];
            rewritten[i] = WithResolvedLayerId(step, layerId);
        }

        return rewritten is null ? plan : plan with { Steps = rewritten };
    }

    public static async Task<AnalysisPlan> ResolveAsync(
        AnalysisPlan plan,
        IProcessCatalog catalog,
        IGeoprocessingRasterSourceResolver? resolver,
        RasterSecurityContextReference securityContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);

        List<AnalysisPlanStep>? rewritten = null;

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (!TryGetReference(step, catalog, out var reference))
            {
                continue;
            }

            if (resolver is null)
            {
                throw new GeoprocessingValidationException(
                    $"Step '{step.StepId}' references a catalog raster by layerId/rasterId, but layer-resolved "
                    + "raster sourcing is not available in this deployment (no raster catalog is configured). "
                    + "Supply an inline base64 'source' raster instead.");
            }

            var resolution = await resolver
                .ResolveAsync(reference, cancellationToken)
                .ConfigureAwait(false);

            if (!resolution.Found || resolution.Descriptor is null)
            {
                throw new GeoprocessingValidationException(
                    $"Step '{step.StepId}' could not resolve its raster source: "
                    + (resolution.FailureReason ?? "the referenced catalog raster was not found."));
            }

            rewritten ??= [.. plan.Steps];
            rewritten[i] = WithResolvedSource(step, resolution.Descriptor, securityContext);
        }

        if (rewritten is null)
        {
            return plan;
        }

        return plan with { Steps = rewritten };
    }

    internal static bool TryGetReference(
        AnalysisPlanStep step,
        IProcessCatalog catalog,
        out RasterSourceReference reference)
    {
        reference = new RasterSourceReference(null, null);

        if (step.Kind != AnalysisPlanStepKind.Geoprocess || string.IsNullOrWhiteSpace(step.ProcessId))
        {
            return false;
        }

        var definition = catalog.GetProcess(step.ProcessId);
        if (definition is null || !DeclaresNativeRasterSource(definition))
        {
            return false;
        }

        // Inline source wins; nothing to resolve.
        if (UsesInlineSource(step, definition))
        {
            return false;
        }

        long? rasterId = null;
        if (step.Inputs.TryGetValue(RasterIdInput, out var rasterRaw)
            && !string.IsNullOrWhiteSpace(rasterRaw)
            && long.TryParse(rasterRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRaster)
            && parsedRaster > 0)
        {
            rasterId = parsedRaster;
        }

        int? layerId = null;
        if (step.Inputs.TryGetValue(LayerIdInput, out var layerRaw)
            && !string.IsNullOrWhiteSpace(layerRaw)
            && int.TryParse(layerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLayer)
            && parsedLayer >= 0)
        {
            layerId = parsedLayer;
        }

        if (rasterId is null && layerId is null)
        {
            return false;
        }

        reference = new RasterSourceReference(layerId, rasterId);
        return true;
    }

    // An inline source wins over a catalog selector for both resolution and authorization, so a stale selector cannot deny a valid inline job.
    internal static bool UsesInlineSource(AnalysisPlanStep step, ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(definition);

        return DeclaresNativeRasterSource(definition)
            && (step.RasterSources.ContainsKey(SourceInput)
                || (step.Inputs.TryGetValue(SourceInput, out var source)
                    && !string.IsNullOrWhiteSpace(source)));
    }

    private static bool DeclaresNativeRasterSource(ProcessDefinition definition)
    {
        var hasSource = false;
        var hasRasterId = false;
        foreach (var parameter in definition.Parameters)
        {
            if (string.Equals(parameter.Name, SourceInput, StringComparison.Ordinal))
            {
                hasSource = true;
            }
            else if (string.Equals(parameter.Name, RasterIdInput, StringComparison.Ordinal))
            {
                hasRasterId = true;
            }
        }

        return hasSource && hasRasterId;
    }

    private static AnalysisPlanStep WithResolvedSource(
        AnalysisPlanStep step,
        RasterSourceDescriptor descriptor,
        RasterSecurityContextReference securityContext)
    {
        // A blank legacy source is semantically absent during plan validation and catalog
        // resolution. Remove it when adding the typed descriptor so the durable worker spec does
        // not contain both source representations and reject a request admission accepted.
        var inputs = new Dictionary<string, string>(step.Inputs, StringComparer.Ordinal);
        inputs.Remove(SourceInput);
        var rasterSources = new Dictionary<string, RasterSourceDescriptor>(
            step.RasterSources,
            StringComparer.Ordinal)
        {
            [SourceInput] = descriptor with { SecurityContext = securityContext },
        };

        return step with { Inputs = inputs, RasterSources = rasterSources };
    }

    private static AnalysisPlanStep WithResolvedLayerId(AnalysisPlanStep step, int layerId)
    {
        var inputs = new Dictionary<string, string>(step.Inputs, StringComparer.Ordinal)
        {
            [LayerIdInput] = layerId.ToString(CultureInfo.InvariantCulture),
        };

        return step with { Inputs = inputs };
    }
}
