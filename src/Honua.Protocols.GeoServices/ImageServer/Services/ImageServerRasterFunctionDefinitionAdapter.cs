// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Outcome of adapting an ImageServer raster-function document to the canonical Core graph.
/// </summary>
internal readonly record struct RasterFunctionDefinitionMapping(
    bool Supported,
    RasterFunctionDefinition? Definition,
    string? Reason,
    bool IsNotImplemented)
{
    public static RasterFunctionDefinitionMapping Executable(RasterFunctionDefinition definition)
        => new(true, definition, null, false);

    public static RasterFunctionDefinitionMapping Invalid(string reason)
        => new(false, null, reason, false);

    public static RasterFunctionDefinitionMapping NotImplemented(string reason)
        => new(false, null, reason, true);
}

/// <summary>
/// Strictly adapts the currently supported, single-input ImageServer raster-function chain to
/// the provider-neutral Core graph. This adapter is intentionally not wired to request execution;
/// later slices can consume the validated definition at an explicit admission boundary.
/// </summary>
internal static class ImageServerRasterFunctionDefinitionAdapter
{
    private const string InputNodeId = "input";
    private const string InputName = "raster";

    // ImageServer's depth contract counts function documents, while the canonical validator
    // also counts the implicit source input node. Keep those definitions aligned explicitly.
    private static readonly RasterFunctionValidationOptions ValidationOptions =
        RasterFunctionValidationOptions.Default with
        {
            MaxDepth = ImageServerRasterFunctionPlanner.MaxChainDepth + 1,
        };

    /// <summary>
    /// Converts an ImageServer function document to a validated canonical definition.
    /// Unsupported, free-form, multi-input, or otherwise ambiguous documents fail closed.
    /// </summary>
    public static RasterFunctionDefinitionMapping Adapt(RasterFunctionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var layers = new List<RasterFunctionDocument>(ImageServerRasterFunctionPlanner.MaxChainDepth);
        var current = document;
        while (true)
        {
            if (!string.IsNullOrWhiteSpace(current.OutputPixelType))
            {
                return RasterFunctionDefinitionMapping.Invalid(
                    "ImageServer outputPixelType overrides are not represented by this canonical function adapter.");
            }

            layers.Add(current);
            var arguments = current.RasterFunctionArguments;
            if (arguments is null || !arguments.TryGetValue("Raster", out var rawRaster))
            {
                break;
            }

            if (rawRaster is not JsonElement { ValueKind: JsonValueKind.Object })
            {
                return RasterFunctionDefinitionMapping.Invalid(
                    "Raster must be a nested raster function document; source references and free-form values are not admitted.");
            }

            if (layers.Count >= ImageServerRasterFunctionPlanner.MaxChainDepth)
            {
                return RasterFunctionDefinitionMapping.Invalid(
                    $"Raster function chain exceeds the maximum depth of {ImageServerRasterFunctionPlanner.MaxChainDepth}.");
            }

            try
            {
                if (!ImageServerRasterFunctionPlanner.TryGetNestedFunction(arguments, "Raster", out var nested))
                {
                    return RasterFunctionDefinitionMapping.Invalid(
                        "Raster must contain a valid nested raster function document.");
                }

                current = nested;
            }
            catch (ImageServerRasterFunctionException exception)
            {
                return RasterFunctionDefinitionMapping.Invalid(exception.Message);
            }
        }

        var nodes = new List<RasterFunctionNode>(layers.Count + 1)
        {
            new RasterFunctionInputNode
            {
                Id = InputNodeId,
                InputName = InputName,
            },
        };

        var inputNodeId = InputNodeId;
        var functionNumber = 0;
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            functionNumber++;
            var nodeId = $"function-{functionNumber}";
            var layer = ImageServerRasterFunctionPlanner.MapCanonicalLayer(
                layers[index],
                nodeId,
                inputNodeId);
            if (!layer.Supported)
            {
                return layer.IsNotImplemented
                    ? RasterFunctionDefinitionMapping.NotImplemented(layer.Reason!)
                    : RasterFunctionDefinitionMapping.Invalid(layer.Reason!);
            }

            nodes.Add(layer.Node!);
            inputNodeId = nodeId;
        }

        var definition = new RasterFunctionDefinition
        {
            Nodes = nodes,
            OutputNodeId = inputNodeId,
        };
        var validation = RasterFunctionValidator.Validate(definition, ValidationOptions);
        if (!validation.IsValid)
        {
            var detail = string.Join(
                "; ",
                validation.Errors.Select(static error => $"{error.Code} at {error.Path}: {error.Message}"));
            return RasterFunctionDefinitionMapping.Invalid(
                $"Generated canonical raster function is invalid: {detail}");
        }

        return RasterFunctionDefinitionMapping.Executable(definition);
    }
}
