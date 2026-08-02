// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Submit-side resolution of native raster/surface process inputs from a registered
/// catalog raster (#2264). Native raster processes read a base64 <c>source</c> the
/// worker decodes; this helper lets a plan instead reference a catalog raster by
/// <c>layerId</c>/<c>rasterId</c> and materializes the resolved bytes onto the
/// <c>source</c> step input BEFORE the spec is projected, so the worker contract is
/// unchanged.
///
/// <para>
/// A step is eligible only when its catalog process declares the native raster-source
/// shape (both a <c>source</c> and a <c>rasterId</c> parameter, the
/// <c>NativeRasterSourceParameters</c> group). Steps that already carry an inline
/// <c>source</c> are left untouched (inline wins). When resolution is required but no
/// resolver is configured, a <see cref="GeoprocessingValidationException"/> is thrown.
/// Registered <c>rasterId</c> references are first bound to their owning layer and sent
/// through the shared authorization gate; unknown or mismatched registrations therefore
/// fail without exposing catalog existence or reading raster bytes.
/// </para>
/// </summary>
internal static class GeoprocessingRasterSourceResolution
{
    private const string SourceInput = "source";
    private const string LayerIdInput = "layerId";
    private const string RasterIdInput = "rasterId";

    /// <summary>
    /// Resolves raster-id selectors to their owning catalog layer and binds that layer onto the
    /// plan before the shared layer gate runs. This phase performs metadata lookup only; raster
    /// bytes are not read until <see cref="ResolveAsync"/> runs after authorization.
    /// </summary>
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

    /// <summary>
    /// Returns a plan whose eligible native-raster steps have their <c>source</c>
    /// populated from the resolved catalog raster. Returns the original plan unchanged
    /// when no step requires resolution.
    /// </summary>
    public static async Task<AnalysisPlan> ResolveAsync(
        AnalysisPlan plan,
        IProcessCatalog catalog,
        IGeoprocessingRasterSourceResolver? resolver,
        long maxBytes,
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
                .ResolveAsync(reference, maxBytes, cancellationToken)
                .ConfigureAwait(false);

            if (!resolution.Found || resolution.Bytes is null)
            {
                throw new GeoprocessingValidationException(
                    $"Step '{step.StepId}' could not resolve its raster source: "
                    + (resolution.FailureReason ?? "the referenced catalog raster was not found."));
            }

            rewritten ??= [.. plan.Steps];
            rewritten[i] = WithResolvedSource(step, resolution.Bytes);
        }

        if (rewritten is null)
        {
            return plan;
        }

        return plan with { Steps = rewritten };
    }

    /// <summary>
    /// Determines whether <paramref name="step"/> must be resolved from a catalog
    /// raster: its process declares the native raster-source shape, it has no inline
    /// <c>source</c>, and it carries a parseable <c>layerId</c> or <c>rasterId</c>.
    /// </summary>
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
        if (step.Inputs.TryGetValue(SourceInput, out var source) && !string.IsNullOrWhiteSpace(source))
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

    private static AnalysisPlanStep WithResolvedSource(AnalysisPlanStep step, byte[] bytes)
    {
        var inputs = new Dictionary<string, string>(step.Inputs, StringComparer.Ordinal)
        {
            [SourceInput] = Convert.ToBase64String(bytes),
        };

        return step with { Inputs = inputs };
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
