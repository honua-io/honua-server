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
/// <param name="Colormap">The resolved pseudocolour colormap, when one applies.</param>
/// <param name="ClipRegion">The resolved clip area-of-interest, when a <c>Clip</c> applies.</param>
/// <param name="Bands">
/// The resolved 1-based band selection/order from an <c>ExtractBand</c> function, when
/// one applies; <c>null</c> when no band extraction is in the chain.
/// </param>
/// <param name="BandArithmetic">
/// The resolved two-band arithmetic operation (e.g. NDVI) from a <c>BandArithmetic</c>
/// function, when one applies; <c>null</c> when no band arithmetic is in the chain.
/// </param>
/// <param name="Reason">Explanation surfaced to the client when not supported.</param>
/// <param name="IsNotImplemented">
/// When unsupported, distinguishes a recognized-but-unimplemented function/option
/// (HTTP 501) from a malformed or unknown chain (HTTP 400).
/// </param>
internal readonly record struct RenderingRuleMapping(
    bool Supported,
    RasterStretch? Stretch,
    RasterColormap? Colormap,
    RasterClipRegion? ClipRegion,
    int[]? Bands,
    RasterBandArithmetic? BandArithmetic,
    string? Reason,
    bool IsNotImplemented)
{
    public static RenderingRuleMapping Executable(
        RasterStretch? stretch,
        RasterColormap? colormap = null,
        RasterClipRegion? clipRegion = null,
        int[]? bands = null,
        RasterBandArithmetic? bandArithmetic = null)
        => new(true, stretch, colormap, clipRegion, bands, bandArithmetic, null, false);

    public static RenderingRuleMapping Invalid(string reason)
        => new(false, null, null, null, null, null, reason, false);

    public static RenderingRuleMapping NotImplemented(string reason)
        => new(false, null, null, null, null, null, reason, true);
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
        "Colormap",
        "Clip",
        "ExtractBand",
        "BandArithmetic",
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
                $"Unsupported raster function '{document.RasterFunction}'. Supported functions: Identity, Stretch, Colormap, Clip, ExtractBand, BandArithmetic.");
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
            case "EXTRACTBAND":
                ValidateExtractBandArguments(arguments);
                break;
            case "BANDARITHMETIC":
                ValidateBandArithmeticArguments(arguments);
                break;
            case "IDENTITY":
            case "COLORMAP":
                // Identity has no required arguments beyond the optional Raster nesting;
                // Colormap validation is enforced on the executable mapping path.
                break;
        }
    }

    private static void ValidateExtractBandArguments(Dictionary<string, object?> arguments)
    {
        if (!arguments.ContainsKey("BandIds") && !arguments.ContainsKey("BandIDs") && !arguments.ContainsKey("BandNames"))
        {
            throw new ImageServerRasterFunctionException(
                "ExtractBand raster function requires a BandIds or BandNames argument.");
        }
    }

    private static void ValidateBandArithmeticArguments(Dictionary<string, object?> arguments)
    {
        if (!arguments.ContainsKey("BandIndexes") && !arguments.ContainsKey("BandIDs") && !arguments.ContainsKey("BandIds"))
        {
            throw new ImageServerRasterFunctionException(
                "BandArithmetic raster function requires a BandIndexes (or BandIds) argument.");
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
    /// Translates a validated raster function <paramref name="document"/> into the
    /// canonical knobs (<see cref="RasterStretch"/>, <see cref="RasterColormap"/>,
    /// <see cref="RasterClipRegion"/>, a band selection, and a <see cref="RasterBandArithmetic"/>)
    /// the shared raster export pipeline executes. Walks an
    /// <c>Identity</c>/<c>Stretch</c>/<c>Colormap</c>/<c>Clip</c>/<c>ExtractBand</c>/<c>BandArithmetic</c>
    /// chain (nested via the <c>Raster</c> argument); an <c>Identity</c>-only chain is
    /// an executable no-op. Recognized-but-unimplemented options
    /// (clip-inside, histogram-equalize/sigmoid stretches, named colour ramps) return a
    /// not-implemented result; unknown functions return an invalid result.
    /// </summary>
    public static RenderingRuleMapping MapRenderingRule(RasterFunctionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        RasterStretch? stretch = null;
        RasterColormap? colormap = null;
        RasterClipRegion? clipRegion = null;
        int[]? bands = null;
        RasterBandArithmetic? bandArithmetic = null;
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
            else if (string.Equals(current.RasterFunction, "Colormap", StringComparison.OrdinalIgnoreCase))
            {
                var mapping = MapColormapArguments(arguments);
                if (!mapping.Supported)
                {
                    return mapping;
                }

                colormap = mapping.Colormap ?? colormap;
            }
            else if (string.Equals(current.RasterFunction, "Clip", StringComparison.OrdinalIgnoreCase))
            {
                var mapping = MapClipArguments(arguments);
                if (!mapping.Supported)
                {
                    return mapping;
                }

                clipRegion = mapping.ClipRegion ?? clipRegion;
            }
            else if (string.Equals(current.RasterFunction, "ExtractBand", StringComparison.OrdinalIgnoreCase))
            {
                var mapping = MapExtractBandArguments(arguments);
                if (!mapping.Supported)
                {
                    return mapping;
                }

                // A later (outer) ExtractBand supersedes an inner one.
                bands = mapping.Bands ?? bands;
            }
            else if (string.Equals(current.RasterFunction, "BandArithmetic", StringComparison.OrdinalIgnoreCase))
            {
                var mapping = MapBandArithmeticArguments(arguments);
                if (!mapping.Supported)
                {
                    return mapping;
                }

                // A later (outer) BandArithmetic supersedes an inner one.
                bandArithmetic = mapping.BandArithmetic ?? bandArithmetic;
            }
            else if (!string.Equals(current.RasterFunction, "Identity", StringComparison.OrdinalIgnoreCase))
            {
                return RenderingRuleMapping.Invalid(
                    $"Unsupported raster function '{current.RasterFunction}'. Supported functions: Identity, Stretch, Colormap, Clip, ExtractBand, BandArithmetic.");
            }

            if (!TryGetNestedFunction(arguments, "Raster", out var nested))
            {
                break;
            }

            current = nested;
        }

        return RenderingRuleMapping.Executable(stretch, colormap, clipRegion, bands, bandArithmetic);
    }

    // BandArithmetic derives a single analytic band from two source bands. Esri encodes the
    // two operands as a BandIndexes (or BandIDs) array of 0-based band indices and selects the
    // formula with Method (NDVI = 3). The canonical raster pipeline uses 1-based bands, so each
    // index is shifted by one. Exactly two band indices are required (visible + infrared). Only
    // the NDVI method is implemented; other Esri band-arithmetic methods surface a clean
    // not-implemented result. By Esri's NDVI convention BandIndexes is [visible, infrared].
    private static RenderingRuleMapping MapBandArithmeticArguments(Dictionary<string, object?> arguments)
    {
        // Method: Esri esriBandArithmeticMethod (3 = NDVI). Default to NDVI when omitted so a
        // bare BandArithmetic with band indices behaves as the common vegetation-index request.
        const int MethodNdvi = 3;
        var method = MethodNdvi;
        if (TryGetInt(arguments, "Method", out var parsedMethod))
        {
            method = parsedMethod;
        }

        if (method != MethodNdvi)
        {
            return RenderingRuleMapping.NotImplemented(
                $"BandArithmetic Method {method.ToString(CultureInfo.InvariantCulture)} is not implemented on this service. Supported method: NDVI (3).");
        }

        if (!TryGetBandArithmeticIndexes(arguments, out var element))
        {
            return RenderingRuleMapping.Invalid(
                "BandArithmetic raster function requires a BandIndexes array of 0-based band indices.");
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return RenderingRuleMapping.Invalid("BandArithmetic BandIndexes must be an array of 0-based band indices.");
        }

        var indices = new List<int>(element.GetArrayLength());
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var zeroBased) || zeroBased < 0)
            {
                return RenderingRuleMapping.Invalid("BandArithmetic BandIndexes entries must be non-negative integers.");
            }

            indices.Add(zeroBased + 1);
        }

        if (indices.Count != 2)
        {
            return RenderingRuleMapping.Invalid(
                "BandArithmetic NDVI requires exactly two band indices: [visible, infrared].");
        }

        var bandArithmetic = new RasterBandArithmetic
        {
            VisibleBand = indices[0],
            InfraredBand = indices[1],
            Method = RasterBandArithmeticMethod.Ndvi,
        };

        return RenderingRuleMapping.Executable(null, null, null, null, bandArithmetic);
    }

    private static bool TryGetBandArithmeticIndexes(Dictionary<string, object?> arguments, out JsonElement element)
    {
        element = default;
        if ((arguments.TryGetValue("BandIndexes", out var raw) ||
             arguments.TryGetValue("BandIDs", out raw) ||
             arguments.TryGetValue("BandIds", out raw)) &&
            raw is JsonElement json)
        {
            element = json;
            return true;
        }

        return false;
    }

    // ExtractBand selects/reorders the output bands. Esri encodes the selection as a
    // BandIds array of 0-based band indices; the canonical raster pipeline uses 1-based
    // indices, so each index is shifted by one before being threaded onto RasterQuery.Bands.
    // Selection by BandNames (string band labels) is recognized but not executable because
    // the catalog does not carry per-band names, so it surfaces a clean not-implemented result.
    private static RenderingRuleMapping MapExtractBandArguments(Dictionary<string, object?> arguments)
    {
        if (!TryGetBandIdsElement(arguments, out var element))
        {
            if (arguments.ContainsKey("BandNames"))
            {
                return RenderingRuleMapping.NotImplemented(
                    "ExtractBand by BandNames is not implemented; supply a BandIds array of 0-based band indices.");
            }

            return RenderingRuleMapping.Invalid(
                "ExtractBand raster function requires a BandIds array of 0-based band indices.");
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            return RenderingRuleMapping.Invalid("ExtractBand BandIds must be a non-empty array of 0-based band indices.");
        }

        var bands = new List<int>(element.GetArrayLength());
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var zeroBased) || zeroBased < 0)
            {
                return RenderingRuleMapping.Invalid("ExtractBand BandIds entries must be non-negative integers.");
            }

            bands.Add(zeroBased + 1);
        }

        return RenderingRuleMapping.Executable(null, null, null, bands.ToArray());
    }

    private static bool TryGetBandIdsElement(Dictionary<string, object?> arguments, out JsonElement element)
    {
        element = default;
        if ((arguments.TryGetValue("BandIds", out var raw) || arguments.TryGetValue("BandIDs", out raw)) &&
            raw is JsonElement json)
        {
            element = json;
            return true;
        }

        return false;
    }

    private static RenderingRuleMapping MapClipArguments(Dictionary<string, object?> arguments)
    {
        // Clip masks the raster to an area of interest. esriClippingType controls which side
        // is kept: 0 (default) keeps pixels inside the geometry; 1 ("clip inside / keep
        // outside") removes pixels inside the geometry and keeps everything outside, executed
        // by the raster store as an inverted clip (RasterClipRegion.Inverted).
        var inverted = false;
        if (TryGetInt(arguments, "ClippingType", out var clippingType))
        {
            if (clippingType == 1)
            {
                inverted = true;
            }
            else if (clippingType != 0)
            {
                return RenderingRuleMapping.NotImplemented(
                    $"Clip ClippingType {clippingType.ToString(CultureInfo.InvariantCulture)} is not implemented. Supported: 0 (keep inside), 1 (keep outside).");
            }
        }

        JsonElement geometry;
        if (arguments.TryGetValue("ClippingGeometry", out var rawGeometry) && rawGeometry is JsonElement geometryElement)
        {
            geometry = geometryElement;
        }
        else if (arguments.TryGetValue("Extent", out var rawExtent) && rawExtent is JsonElement extentElement)
        {
            geometry = extentElement;
        }
        else
        {
            return RenderingRuleMapping.Invalid("Clip raster function requires a ClippingGeometry or Extent argument.");
        }

        if (!TryBuildClipRegion(geometry, inverted, out var region))
        {
            return RenderingRuleMapping.Invalid("Clip geometry must be an Esri envelope or polygon.");
        }

        return RenderingRuleMapping.Executable(null, null, region);
    }

    private static bool TryBuildClipRegion(JsonElement geometry, bool inverted, out RasterClipRegion region)
    {
        region = default;
        if (geometry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int? srid = null;
        if (geometry.TryGetProperty("spatialReference", out var sr) && sr.ValueKind == JsonValueKind.Object)
        {
            if ((sr.TryGetProperty("latestWkid", out var latest) && latest.ValueKind == JsonValueKind.Number && latest.TryGetInt32(out var latestWkid))
                || (sr.TryGetProperty("wkid", out var wkid) && wkid.ValueKind == JsonValueKind.Number && wkid.TryGetInt32(out latestWkid)))
            {
                srid = latestWkid;
            }
        }

        NetTopologySuite.Geometries.Geometry? nts = null;
        if (geometry.TryGetProperty("rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
        {
            nts = TryBuildPolygon(rings);
        }
        else if (geometry.TryGetProperty("xmin", out _))
        {
            nts = TryBuildEnvelope(geometry);
        }

        if (nts is null)
        {
            return false;
        }

        region = new RasterClipRegion
        {
            Geometry = new NetTopologySuite.IO.WKBWriter().Write(nts),
            Srid = srid,
            Inverted = inverted,
        };
        return true;
    }

    private static NetTopologySuite.Geometries.Geometry? TryBuildEnvelope(JsonElement element)
    {
        if (!TryGetDoubleProperty(element, "xmin", out var xmin) ||
            !TryGetDoubleProperty(element, "ymin", out var ymin) ||
            !TryGetDoubleProperty(element, "xmax", out var xmax) ||
            !TryGetDoubleProperty(element, "ymax", out var ymax))
        {
            return null;
        }

        return new NetTopologySuite.Geometries.GeometryFactory().ToGeometry(
            new NetTopologySuite.Geometries.Envelope(xmin, xmax, ymin, ymax));
    }

    private static NetTopologySuite.Geometries.Polygon? TryBuildPolygon(JsonElement rings)
    {
        if (rings.GetArrayLength() == 0)
        {
            return null;
        }

        var factory = new NetTopologySuite.Geometries.GeometryFactory();
        var linearRings = new List<NetTopologySuite.Geometries.LinearRing>();
        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4)
            {
                return null;
            }

            var coordinates = new List<NetTopologySuite.Geometries.Coordinate>(ring.GetArrayLength());
            foreach (var point in ring.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2 ||
                    !point[0].TryGetDouble(out var x) || !point[1].TryGetDouble(out var y))
                {
                    return null;
                }

                coordinates.Add(new NetTopologySuite.Geometries.Coordinate(x, y));
            }

            try
            {
                linearRings.Add(factory.CreateLinearRing(coordinates.ToArray()));
            }
            catch (ArgumentException)
            {
                return null; // not a closed/valid ring
            }
        }

        // First ring is the exterior; the remainder are holes.
        var holes = linearRings.Count > 1 ? linearRings.GetRange(1, linearRings.Count - 1).ToArray() : null;
        return factory.CreatePolygon(linearRings[0], holes);
    }

    private static bool TryGetDoubleProperty(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var child) &&
               child.ValueKind == JsonValueKind.Number &&
               child.TryGetDouble(out value);
    }

    private static RenderingRuleMapping MapColormapArguments(Dictionary<string, object?> arguments)
    {
        // Esri supports a named ColorrampName or an explicit Colormap array of
        // [value, r, g, b] (optionally with a) stops. An explicit array wins; otherwise a
        // recognised ColorrampName is resolved to anchor stops over the display range.
        if (!arguments.TryGetValue("Colormap", out var raw) || raw is not JsonElement element)
        {
            if (TryGetString(arguments, "ColorrampName", out var rampName))
            {
                if (NamedColorRamp.TryResolve(rampName, out var namedColormap))
                {
                    return RenderingRuleMapping.Executable(null, namedColormap);
                }

                return RenderingRuleMapping.NotImplemented(
                    $"Colormap ColorrampName '{rampName}' is not implemented. Supported named ramps: {NamedColorRamp.SupportedNamesText()}. " +
                    "Alternatively supply an explicit Colormap array of [value, r, g, b].");
            }

            if (arguments.ContainsKey("Colorramp"))
            {
                // A "Colorramp" object (algorithmic/multipart ramp definition) is a richer
                // structure than a named ramp; resolving it requires evaluating the algorithm,
                // which is deferred. Named ramps and explicit stop arrays are supported.
                return RenderingRuleMapping.NotImplemented(
                    "Colormap by inline Colorramp object is not implemented; supply a ColorrampName or an explicit Colormap array of [value, r, g, b].");
            }

            return RenderingRuleMapping.Invalid("Colormap raster function requires a Colormap array of [value, r, g, b] stops or a ColorrampName.");
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            return RenderingRuleMapping.Invalid("Colormap must be a non-empty array of [value, r, g, b] stops.");
        }

        var entries = new List<RasterColormapEntry>(element.GetArrayLength());
        foreach (var stop in element.EnumerateArray())
        {
            if (stop.ValueKind != JsonValueKind.Array || stop.GetArrayLength() < 4)
            {
                return RenderingRuleMapping.Invalid("Each Colormap stop must be [value, r, g, b] (alpha optional).");
            }

            if (!stop[0].TryGetDouble(out var value))
            {
                return RenderingRuleMapping.Invalid("Colormap stop value must be numeric.");
            }

            var red = ClampChannel(stop[1]);
            var green = ClampChannel(stop[2]);
            var blue = ClampChannel(stop[3]);
            var alpha = stop.GetArrayLength() >= 5 ? ClampChannel(stop[4]) : (byte)255;
            entries.Add(new RasterColormapEntry(value, red, green, blue, alpha));
        }

        return RenderingRuleMapping.Executable(null, new RasterColormap { Entries = entries });
    }

    private static byte ClampChannel(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        }

        return 0;
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

    private static bool TryGetString(Dictionary<string, object?> arguments, string key, out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.String:
                value = json.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            case string s:
                value = s;
                return !string.IsNullOrWhiteSpace(value);
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
