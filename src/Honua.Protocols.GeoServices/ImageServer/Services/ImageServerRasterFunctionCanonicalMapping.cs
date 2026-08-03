// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Result of mapping one admitted ImageServer function layer to a canonical node.
/// </summary>
internal readonly record struct CanonicalRasterFunctionNodeMapping(
    bool Supported,
    RasterFunctionNode? Node,
    string? Reason,
    bool IsNotImplemented)
{
    public static CanonicalRasterFunctionNodeMapping Executable(RasterFunctionNode node)
        => new(true, node, null, false);

    public static CanonicalRasterFunctionNodeMapping Invalid(string reason)
        => new(false, null, reason, false);

    public static CanonicalRasterFunctionNodeMapping NotImplemented(string reason)
        => new(false, null, reason, true);
}

internal sealed partial class ImageServerRasterFunctionPlanner
{
    /// <summary>
    /// Maps exactly one ImageServer function layer through the existing typed rendering-rule
    /// parsers. This is the canonical adapter seam; chain traversal and node ordering remain
    /// the adapter's responsibility.
    /// </summary>
    internal static CanonicalRasterFunctionNodeMapping MapCanonicalLayer(
        RasterFunctionDocument document,
        string nodeId,
        string inputNodeId)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(document.RasterFunction))
        {
            return CanonicalRasterFunctionNodeMapping.Invalid("rasterFunction is required.");
        }

        if (!SupportedFunctions.Contains(document.RasterFunction))
        {
            return CanonicalRasterFunctionNodeMapping.Invalid(
                $"Unsupported raster function '{document.RasterFunction}'. Supported functions: {SupportedFunctionsText}");
        }

        var arguments = document.RasterFunctionArguments ?? new Dictionary<string, object?>();
        var strictFailure = ValidateCanonicalArguments(document.RasterFunction, arguments);
        if (strictFailure is not null)
        {
            return CanonicalRasterFunctionNodeMapping.Invalid(strictFailure);
        }

        if (string.Equals(document.RasterFunction, "Identity", StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalRasterFunctionNodeMapping.Executable(new RasterFunctionIdentityNode
            {
                Id = nodeId,
                Inputs = [inputNodeId],
            });
        }

        var mapping = document.RasterFunction.ToUpperInvariant() switch
        {
            "STRETCH" => MapStretchArguments(arguments),
            "COLORMAP" => MapColormapArguments(arguments),
            "CLIP" => MapClipArguments(arguments),
            "EXTRACTBAND" => MapExtractBandArguments(arguments),
            "BANDARITHMETIC" => MapBandArithmeticArguments(arguments),
            "HILLSHADE" or "SLOPE" or "ASPECT" => MapTerrainArguments(document.RasterFunction, arguments),
            _ => RenderingRuleMapping.Invalid($"Unsupported raster function '{document.RasterFunction}'."),
        };

        if (!mapping.Supported)
        {
            return mapping.IsNotImplemented
                ? CanonicalRasterFunctionNodeMapping.NotImplemented(mapping.Reason!)
                : CanonicalRasterFunctionNodeMapping.Invalid(mapping.Reason!);
        }

        var node = CreateCanonicalNode(document.RasterFunction, nodeId, inputNodeId, mapping);
        return node is null
            ? CanonicalRasterFunctionNodeMapping.Invalid(
                $"Raster function '{document.RasterFunction}' did not resolve to a canonical operation.")
            : CanonicalRasterFunctionNodeMapping.Executable(node);
    }

    private static RasterFunctionNode? CreateCanonicalNode(
        string function,
        string nodeId,
        string inputNodeId,
        RenderingRuleMapping mapping)
    {
        IReadOnlyList<string> inputs = [inputNodeId];
        switch (function.ToUpperInvariant())
        {
            case "STRETCH":
                // The canonical graph has no Stretch(None) node. Preserve the explicit no-op
                // as Identity instead of dropping a layer and changing chain topology.
                return mapping.Stretch is { } stretch
                    ? new RasterFunctionStretchNode { Id = nodeId, Inputs = inputs, Stretch = stretch }
                    : new RasterFunctionIdentityNode { Id = nodeId, Inputs = inputs };
            case "COLORMAP" when mapping.Colormap is { } colormap:
                return new RasterFunctionColormapNode { Id = nodeId, Inputs = inputs, Colormap = colormap };
            case "CLIP" when mapping.ClipRegion is { } clip:
                return new RasterFunctionClipNode { Id = nodeId, Inputs = inputs, Region = clip };
            case "EXTRACTBAND" when mapping.Bands is { } bands:
                return new RasterFunctionBandSelectNode { Id = nodeId, Inputs = inputs, Bands = bands };
            case "BANDARITHMETIC" when mapping.BandArithmetic is { } arithmetic:
                var spectralMethod = arithmetic.Method switch
                {
                    RasterBandArithmeticMethod.Ndvi => RasterSpectralIndexMethod.Ndvi,
                    RasterBandArithmeticMethod.Ndwi => RasterSpectralIndexMethod.Ndwi,
                    RasterBandArithmeticMethod.Savi => RasterSpectralIndexMethod.Savi,
                    _ => (RasterSpectralIndexMethod?)null,
                };
                if (spectralMethod is null)
                {
                    return null;
                }

                return new RasterFunctionSpectralIndexNode
                {
                    Id = nodeId,
                    Inputs = inputs,
                    Method = spectralMethod.Value,
                    PrimaryBand = arithmetic.InfraredBand,
                    SecondaryBand = arithmetic.VisibleBand,
                };
            case "HILLSHADE" or "SLOPE" or "ASPECT" when mapping.Terrain is { } terrain:
                return new RasterFunctionTerrainNode { Id = nodeId, Inputs = inputs, Terrain = terrain };
            default:
                return null;
        }
    }

    private static string? ValidateCanonicalArguments(
        string function,
        Dictionary<string, object?> arguments)
    {
        var normalized = function.ToUpperInvariant();
        var allowed = normalized switch
        {
            "IDENTITY" => new[] { "Raster" },
            "STRETCH" => ["Raster", "StretchType", "NumberOfStandardDeviations", "MinPercent", "MaxPercent", "Statistics"],
            "COLORMAP" => ["Raster", "Colormap", "ColorrampName", "Colorramp", "ColorRamp"],
            "CLIP" => ["Raster", "ClippingGeometry", "Extent", "ClippingType"],
            "EXTRACTBAND" => ["Raster", "BandIds", "BandIDs", "BandNames"],
            "BANDARITHMETIC" => ["Raster", "BandIndexes", "BandIDs", "BandIds", "Method"],
            "HILLSHADE" => ["Raster", "BandId", "BandIndex", "ZFactor", "Azimuth", "Altitude"],
            "SLOPE" or "ASPECT" => ["Raster", "BandId", "BandIndex", "ZFactor"],
            _ => Array.Empty<string>(),
        };

        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var key in arguments.Keys)
        {
            if (!allowedSet.Contains(key))
            {
                return $"Raster function '{function}' contains unsupported or free-form argument '{key}'.";
            }
        }

        return normalized switch
        {
            "STRETCH" => ValidateCanonicalStretch(arguments),
            "COLORMAP" => ValidateCanonicalColormap(arguments),
            "CLIP" => ValidateCanonicalClip(arguments),
            "EXTRACTBAND" => ValidateExclusiveAliases(arguments, "ExtractBand", "BandIds", "BandIDs", "BandNames"),
            "BANDARITHMETIC" => ValidateCanonicalBandArithmetic(arguments),
            "HILLSHADE" => ValidateCanonicalTerrain(arguments, function, includeIllumination: true),
            "SLOPE" or "ASPECT" => ValidateCanonicalTerrain(arguments, function, includeIllumination: false),
            _ => null,
        };
    }

    private static string? ValidateCanonicalStretch(Dictionary<string, object?> arguments)
    {
        if (!TryGetInt(arguments, "StretchType", out var stretchType))
        {
            return "Stretch raster function requires an integer StretchType argument.";
        }

        if (arguments.ContainsKey("NumberOfStandardDeviations"))
        {
            if (stretchType != StretchTypeStandardDeviation)
            {
                return "Stretch NumberOfStandardDeviations is ambiguous for the selected StretchType.";
            }

            if (!TryGetDouble(arguments, "NumberOfStandardDeviations", out var deviations)
                || !double.IsFinite(deviations)
                || deviations <= 0)
            {
                return "Stretch NumberOfStandardDeviations must be a finite number greater than zero.";
            }
        }

        var hasPercentArguments = arguments.ContainsKey("MinPercent") || arguments.ContainsKey("MaxPercent");
        if (hasPercentArguments && stretchType != StretchTypePercentClip)
        {
            return "Stretch MinPercent and MaxPercent are ambiguous for the selected StretchType.";
        }

        foreach (var key in new[] { "MinPercent", "MaxPercent" })
        {
            if (arguments.ContainsKey(key)
                && (!TryGetDouble(arguments, key, out var percentage)
                    || !double.IsFinite(percentage)
                    || percentage is < 0 or > 100))
            {
                return $"Stretch {key} must be a finite percentage between 0 and 100.";
            }
        }

        if (stretchType == StretchTypeNone && arguments.ContainsKey("Statistics"))
        {
            return "Stretch Statistics are ambiguous when StretchType is None.";
        }

        return ValidateCanonicalStatistics(arguments);
    }

    private static string? ValidateCanonicalStatistics(Dictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("Statistics", out var raw))
        {
            return null;
        }

        if (raw is not JsonElement element
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() == 0)
        {
            return "Stretch Statistics must be a non-empty array of per-band numeric statistics.";
        }

        foreach (var band in element.EnumerateArray())
        {
            if (band.ValueKind != JsonValueKind.Array
                || band.GetArrayLength() is < 2 or > 4)
            {
                return "Stretch Statistics entries must contain two to four finite numeric values.";
            }

            foreach (var statistic in band.EnumerateArray())
            {
                if (statistic.ValueKind != JsonValueKind.Number
                    || !statistic.TryGetDouble(out var value)
                    || !double.IsFinite(value))
                {
                    return "Stretch Statistics entries must contain two to four finite numeric values.";
                }
            }
        }

        return null;
    }

    private static string? ValidateCanonicalColormap(Dictionary<string, object?> arguments)
    {
        var aliasFailure = ValidateExclusiveAliases(
            arguments,
            "Colormap",
            "Colormap",
            "ColorrampName",
            "Colorramp",
            "ColorRamp");
        if (aliasFailure is not null)
        {
            return aliasFailure;
        }

        if (!arguments.TryGetValue("Colormap", out var raw))
        {
            return null;
        }

        if (raw is not JsonElement element
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() == 0)
        {
            return "Colormap must be a non-empty array of [value, r, g, b] stops.";
        }

        foreach (var stop in element.EnumerateArray())
        {
            if (stop.ValueKind != JsonValueKind.Array || stop.GetArrayLength() is < 4 or > 5)
            {
                return "Each Colormap stop must contain exactly [value, r, g, b] with optional alpha.";
            }

            for (var index = 0; index < stop.GetArrayLength(); index++)
            {
                if (stop[index].ValueKind != JsonValueKind.Number
                    || !stop[index].TryGetDouble(out var value)
                    || !double.IsFinite(value))
                {
                    return "Colormap stop values and channels must be finite numbers.";
                }

                if (index > 0 && value is < 0 or > 255)
                {
                    return "Colormap colour channels must be between 0 and 255.";
                }
            }
        }

        return null;
    }

    private static string? ValidateCanonicalClip(Dictionary<string, object?> arguments)
    {
        var aliasFailure = ValidateExclusiveAliases(arguments, "Clip", "ClippingGeometry", "Extent");
        if (aliasFailure is not null)
        {
            return aliasFailure;
        }

        if (arguments.ContainsKey("ClippingType") && !TryGetInt(arguments, "ClippingType", out _))
        {
            return "Clip ClippingType must be an integer.";
        }

        return null;
    }

    private static string? ValidateCanonicalBandArithmetic(Dictionary<string, object?> arguments)
    {
        var aliasFailure = ValidateExclusiveAliases(
            arguments,
            "BandArithmetic",
            "BandIndexes",
            "BandIDs",
            "BandIds");
        if (aliasFailure is not null)
        {
            return aliasFailure;
        }

        if (arguments.ContainsKey("Method") && !TryGetInt(arguments, "Method", out _))
        {
            return "BandArithmetic Method must be an integer.";
        }

        return null;
    }

    private static string? ValidateCanonicalTerrain(
        Dictionary<string, object?> arguments,
        string function,
        bool includeIllumination)
    {
        if (arguments.ContainsKey("BandId") && arguments.ContainsKey("BandIndex"))
        {
            return $"{function} BandId and BandIndex aliases are ambiguous when supplied together.";
        }

        foreach (var key in new[] { "BandId", "BandIndex" })
        {
            if (arguments.ContainsKey(key) && !TryGetInt(arguments, key, out _))
            {
                return $"{function} {key} must be an integer.";
            }
        }

        var numericKeys = includeIllumination
            ? new[] { "ZFactor", "Azimuth", "Altitude" }
            : new[] { "ZFactor" };
        foreach (var key in numericKeys)
        {
            if (arguments.ContainsKey(key)
                && (!TryGetDouble(arguments, key, out var value) || !double.IsFinite(value)))
            {
                return $"{function} {key} must be a finite number.";
            }
        }

        return null;
    }

    private static string? ValidateExclusiveAliases(
        Dictionary<string, object?> arguments,
        string function,
        params string[] aliases)
    {
        var supplied = aliases.Count(arguments.ContainsKey);
        if (supplied > 1)
        {
            return $"{function} aliases {string.Join(", ", aliases)} are ambiguous when supplied together.";
        }

        return supplied == 0
            ? $"{function} requires exactly one of: {string.Join(", ", aliases)}."
            : null;
    }
}
