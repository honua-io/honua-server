// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;
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
/// Outcome of translating an Esri <c>renderingRule</c> chain into the canonical
/// <see cref="RasterStretch"/> the raster store executes.
/// </summary>
/// <param name="Supported">
/// <c>true</c> when the chain is executable. <see cref="Stretch"/> is the resolved
/// stretch, or <c>null</c> when the chain is a no-op (e.g. <c>Identity</c> only).
/// </param>
/// <param name="Stretch">The resolved display stretch, when one applies.</param>
/// <param name="Reason">Explanation surfaced to the client when not supported.</param>
/// <param name="IsNotImplemented">
/// When unsupported, distinguishes a recognized-but-unimplemented function/option
/// (HTTP 501) from a malformed or unknown chain (HTTP 400).
/// </param>
internal readonly record struct RenderingRuleMapping(
    bool Supported,
    RasterStretch? Stretch,
    string? Reason,
    bool IsNotImplemented)
{
    public static RenderingRuleMapping Executable(RasterStretch? stretch)
        => new(true, stretch, null, false);

    public static RenderingRuleMapping Invalid(string reason)
        => new(false, null, reason, false);

    public static RenderingRuleMapping NotImplemented(string reason)
        => new(false, null, reason, true);
}

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

        // Esri encodes StretchType as the integer esriRasterStretchType value
        // (3=StandardDeviations, 5=MinimumMaximum, 6=PercentMinimumMaximum, etc.).
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

    // Esri esriRasterStretchType integer values for the StretchType the service executes.
    private const int StretchTypeNone = 0;
    private const int StretchTypeStandardDeviation = 3;
    private const int StretchTypeMinMax = 5;
    private const int StretchTypePercentClip = 6;

    /// <summary>
    /// Translates a validated raster function <paramref name="document"/> into a
    /// canonical <see cref="RasterStretch"/>. Walks an <c>Identity</c>/<c>Stretch</c>
    /// chain (nested via the <c>Raster</c> argument); an <c>Identity</c>-only chain is
    /// an executable no-op. Recognized-but-unimplemented functions/options
    /// (<c>Clip</c> in a rule, histogram-equalize/sigmoid stretches) return a
    /// not-implemented result; unknown functions return an invalid result.
    /// </summary>
    public static RenderingRuleMapping MapRenderingRule(RasterFunctionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        RasterStretch? stretch = null;
        var current = document;
        for (var depth = 1; ; depth++)
        {
            if (depth > MaxChainDepth)
            {
                return RenderingRuleMapping.Invalid(
                    $"Raster function chain exceeds the maximum depth of {MaxChainDepth}.");
            }

            if (string.IsNullOrWhiteSpace(current.RasterFunction))
            {
                return RenderingRuleMapping.Invalid("rasterFunction is required.");
            }

            var arguments = current.RasterFunctionArguments ?? new Dictionary<string, object?>();

            if (string.Equals(current.RasterFunction, "Stretch", StringComparison.OrdinalIgnoreCase))
            {
                var mapping = MapStretchArguments(arguments);
                if (!mapping.Supported)
                {
                    return mapping;
                }

                // A later (outer) Stretch in the chain supersedes an inner one.
                stretch = mapping.Stretch ?? stretch;
            }
            else if (string.Equals(current.RasterFunction, "Clip", StringComparison.OrdinalIgnoreCase))
            {
                return RenderingRuleMapping.NotImplemented(
                    "Clip raster function is not supported inside renderingRule on this service. " +
                    "Clip with the export bbox or geometry parameters instead.");
            }
            else if (!string.Equals(current.RasterFunction, "Identity", StringComparison.OrdinalIgnoreCase))
            {
                return RenderingRuleMapping.Invalid(
                    $"Unsupported raster function '{current.RasterFunction}'. Supported functions: Identity, Stretch.");
            }

            if (!TryGetNestedFunction(arguments, "Raster", out var nested))
            {
                break;
            }

            current = nested;
        }

        return RenderingRuleMapping.Executable(stretch);
    }

    private static RenderingRuleMapping MapStretchArguments(Dictionary<string, object?> arguments)
    {
        if (!TryGetInt(arguments, "StretchType", out var stretchType))
        {
            return RenderingRuleMapping.Invalid("Stretch raster function requires an integer StretchType argument.");
        }

        switch (stretchType)
        {
            case StretchTypeNone:
                return RenderingRuleMapping.Executable(null);

            case StretchTypeMinMax:
                return RenderingRuleMapping.Executable(WithStatistics(
                    new RasterStretch { StretchType = RasterStretchType.MinMax },
                    arguments));

            case StretchTypeStandardDeviation:
                {
                    var stretch = new RasterStretch { StretchType = RasterStretchType.StandardDeviation };
                    if (TryGetDouble(arguments, "NumberOfStandardDeviations", out var n) && n > 0)
                    {
                        stretch = stretch with { NumberOfStandardDeviations = n };
                    }

                    return RenderingRuleMapping.Executable(WithStatistics(stretch, arguments));
                }

            case StretchTypePercentClip:
                {
                    var stretch = new RasterStretch { StretchType = RasterStretchType.PercentClip };
                    if (TryGetDouble(arguments, "MinPercent", out var minPercent) && minPercent >= 0)
                    {
                        stretch = stretch with { MinPercent = minPercent };
                    }

                    if (TryGetDouble(arguments, "MaxPercent", out var maxPercent) && maxPercent >= 0)
                    {
                        stretch = stretch with { MaxPercent = maxPercent };
                    }

                    return RenderingRuleMapping.Executable(WithStatistics(stretch, arguments));
                }

            default:
                return RenderingRuleMapping.NotImplemented(
                    $"Stretch StretchType {stretchType.ToString(CultureInfo.InvariantCulture)} is not implemented on this service. " +
                    "Supported types: MinMax (5), StandardDeviation (3), PercentClip (6).");
        }
    }

    // Esri supplies precomputed per-band statistics as Statistics: [[min, max, mean, stddev], ...].
    private static RasterStretch WithStatistics(RasterStretch stretch, Dictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("Statistics", out var raw) ||
            raw is not JsonElement element ||
            element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() == 0)
        {
            return stretch;
        }

        var mins = new List<double>();
        var maxes = new List<double>();
        foreach (var band in element.EnumerateArray())
        {
            if (band.ValueKind != JsonValueKind.Array || band.GetArrayLength() < 2)
            {
                return stretch;
            }

            if (!band[0].TryGetDouble(out var min) || !band[1].TryGetDouble(out var max))
            {
                return stretch;
            }

            mins.Add(min);
            maxes.Add(max);
        }

        return stretch with { StatisticsMin = mins.ToArray(), StatisticsMax = maxes.ToArray() };
    }

    private static bool TryGetInt(Dictionary<string, object?> arguments, string key, out int value)
    {
        value = 0;
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromString):
                value = fromString;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = (int)l;
                return true;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromBoxed):
                value = fromBoxed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetDouble(Dictionary<string, object?> arguments, string key, out double value)
    {
        value = 0;
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var parsed):
                value = parsed;
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.String && double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fromString):
                value = fromString;
                return true;
            case double d:
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var fromBoxed):
                value = fromBoxed;
                return true;
            default:
                return false;
        }
    }
}
