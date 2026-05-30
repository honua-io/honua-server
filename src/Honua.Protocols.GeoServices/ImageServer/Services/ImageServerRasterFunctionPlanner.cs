// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Validates and walks Esri raster function chain documents. The MVP supports
/// the three function names that ArcGIS Pro consistently emits when applying a
/// stretch with an AOI clip on a single-band raster: <c>Identity</c>,
/// <c>Stretch</c>, and <c>Clip</c>. Unsupported functions surface as a 400 with
/// an explicit reason rather than failing silently.
/// </summary>
internal interface IImageServerRasterFunctionPlanner
{
    /// <summary>
    /// Walks the function chain and produces a normalised plan describing the
    /// depth, the functions executed in depth-first order, and the output pixel type.
    /// </summary>
    /// <exception cref="ImageServerRasterFunctionException">
    /// Thrown when the chain references an unknown function or when validation fails.
    /// </exception>
    RasterFunctionPlan Plan(RasterFunctionDocument document);
}

/// <summary>
/// Result of <see cref="IImageServerRasterFunctionPlanner.Plan"/>.
/// </summary>
internal readonly record struct RasterFunctionPlan(
    int ChainDepth,
    string[] ExecutedFunctions,
    string OutputPixelType);

/// <summary>
/// Surfaces a raster function chain validation failure to the calling endpoint as a 400.
/// </summary>
internal sealed class ImageServerRasterFunctionException : Exception
{
    public ImageServerRasterFunctionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Default <see cref="IImageServerRasterFunctionPlanner"/>. The chain depth is
/// capped to keep recursive payloads bounded.
/// </summary>
internal sealed class ImageServerRasterFunctionPlanner : IImageServerRasterFunctionPlanner
{
    /// <summary>Maximum depth permitted for nested raster function chains.</summary>
    public const int MaxChainDepth = 8;

    private static readonly HashSet<string> SupportedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Identity",
        "Stretch",
        "Clip",
    };

    public RasterFunctionPlan Plan(RasterFunctionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(document.RasterFunction))
        {
            throw new ImageServerRasterFunctionException("rasterFunction is required.");
        }

        var executed = new List<string>(MaxChainDepth);
        var depth = Walk(document, executed, 1);
        var outputPixelType = !string.IsNullOrWhiteSpace(document.OutputPixelType)
            ? document.OutputPixelType
            : InferOutputPixelType(executed);

        return new RasterFunctionPlan(depth, executed.ToArray(), outputPixelType);
    }

    private static int Walk(RasterFunctionDocument document, List<string> executed, int currentDepth)
    {
        if (currentDepth > MaxChainDepth)
        {
            throw new ImageServerRasterFunctionException(
                $"Raster function chain exceeds the maximum depth of {MaxChainDepth}.");
        }

        if (!SupportedFunctions.Contains(document.RasterFunction))
        {
            throw new ImageServerRasterFunctionException(
                $"Unsupported raster function '{document.RasterFunction}'. Supported functions: Identity, Stretch, Clip.");
        }

        executed.Add(document.RasterFunction);

        // Validate even when the arguments bag is empty so that required keys
        // (Stretch.StretchType, Clip.ClippingGeometry/Extent) are still enforced.
        var arguments = document.RasterFunctionArguments ?? new Dictionary<string, object?>();
        ValidateArguments(document.RasterFunction, arguments);

        if (arguments.Count == 0)
        {
            return currentDepth;
        }

        // Esri raster function chains nest by passing another function document
        // through the "Raster" or "Raster2" argument. The MVP only walks "Raster".
        if (TryGetNestedFunction(arguments, "Raster", out var nested))
        {
            return Walk(nested, executed, currentDepth + 1);
        }

        return currentDepth;
    }

    private static void ValidateArguments(string function, Dictionary<string, object?> arguments)
    {
        switch (function.ToUpperInvariant())
        {
            case "STRETCH":
                ValidateStretchArguments(arguments);
                break;
            case "CLIP":
                ValidateClipArguments(arguments);
                break;
            case "IDENTITY":
                // Identity has no required arguments beyond the optional Raster nesting.
                break;
        }
    }

    private static void ValidateStretchArguments(Dictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("StretchType", out var stretchType) || stretchType is null)
        {
            throw new ImageServerRasterFunctionException("Stretch raster function requires a StretchType argument.");
        }

        // Esri encodes StretchType as the integer enum value (3=MinMax, 5=PercentClip, etc.).
        if (stretchType is JsonElement json && json.ValueKind == JsonValueKind.Number)
        {
            return;
        }

        if (stretchType is int or long)
        {
            return;
        }

        if (stretchType is string s && int.TryParse(s, out _))
        {
            return;
        }

        throw new ImageServerRasterFunctionException(
            "Stretch StretchType must be an integer matching an Esri esriRasterStretchType value.");
    }

    private static void ValidateClipArguments(Dictionary<string, object?> arguments)
    {
        if (!arguments.ContainsKey("ClippingGeometry") && !arguments.ContainsKey("Extent"))
        {
            throw new ImageServerRasterFunctionException(
                "Clip raster function requires either ClippingGeometry or Extent.");
        }
    }

    private static bool TryGetNestedFunction(
        Dictionary<string, object?> arguments,
        string key,
        out RasterFunctionDocument document)
    {
        document = null!;

        if (!arguments.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        // Source-generated JSON delivers nested function chains as JsonElement objects.
        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var json = element.GetRawText();
                var nested = JsonSerializer.Deserialize(json, ImageServerJsonContext.Default.RasterFunctionDocument);
                if (nested is null)
                {
                    return false;
                }

                document = nested;
                return true;
            }
            catch (JsonException)
            {
                throw new ImageServerRasterFunctionException(
                    $"Nested raster function under '{key}' is not a valid raster function document.");
            }
        }

        return false;
    }

    private static string InferOutputPixelType(List<string> executed)
    {
        // Stretch always produces an 8-bit unsigned output by Esri convention.
        for (var i = 0; i < executed.Count; i++)
        {
            if (string.Equals(executed[i], "Stretch", StringComparison.OrdinalIgnoreCase))
            {
                return "U8";
            }
        }
        return "F32";
    }
}
