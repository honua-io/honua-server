// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster.Functions;

/// <summary>Version constants for the canonical raster-function graph contract.</summary>
public static class RasterFunctionContract
{
    /// <summary>The earliest graph version understood by this release.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The graph version written by this release.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// A reusable, provider-neutral raster-function graph. Input nodes name binding slots;
/// an invocation binds those slots to typed raster source descriptors separately.
/// </summary>
public sealed record RasterFunctionDefinition
{
    /// <summary>The version of this graph contract.</summary>
    [JsonRequired]
    public int ContractVersion { get; init; } = RasterFunctionContract.CurrentVersion;

    /// <summary>All nodes in the graph. Node order has no execution meaning.</summary>
    public required IReadOnlyList<RasterFunctionNode> Nodes { get; init; }

    /// <summary>Identifier of the node whose value is the graph output.</summary>
    public required string OutputNodeId { get; init; }
}

/// <summary>Binds a canonical function definition to immutable typed raster sources.</summary>
public sealed record RasterFunctionInvocation
{
    /// <summary>The validated function graph.</summary>
    public required RasterFunctionDefinition Definition { get; init; }

    /// <summary>Typed sources keyed by the <see cref="RasterFunctionInputNode.InputName"/> slots.</summary>
    public required IReadOnlyDictionary<string, RasterSourceDescriptor> Sources { get; init; }
}

/// <summary>A typed node in a canonical raster-function graph.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "nodeType")]
[JsonDerivedType(typeof(RasterFunctionInputNode), "input")]
[JsonDerivedType(typeof(RasterFunctionIdentityNode), "identity")]
[JsonDerivedType(typeof(RasterFunctionBandSelectNode), "band-select")]
[JsonDerivedType(typeof(RasterFunctionSpectralIndexNode), "spectral-index")]
[JsonDerivedType(typeof(RasterFunctionClipNode), "clip")]
[JsonDerivedType(typeof(RasterFunctionResampleNode), "resample")]
[JsonDerivedType(typeof(RasterFunctionReprojectNode), "reproject")]
[JsonDerivedType(typeof(RasterFunctionStretchNode), "stretch")]
[JsonDerivedType(typeof(RasterFunctionColormapNode), "colormap")]
[JsonDerivedType(typeof(RasterFunctionTerrainNode), "terrain")]
[JsonDerivedType(typeof(RasterFunctionReclassifyNode), "reclassify")]
[JsonDerivedType(typeof(RasterFunctionCompositeNode), "composite")]
public abstract record RasterFunctionNode
{
    /// <summary>Definition-local stable identifier.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Ordered upstream node identifiers. Ordering is semantically significant for nodes
    /// with more than one input.
    /// </summary>
    public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();
}

/// <summary>Declares a named raster input slot.</summary>
public sealed record RasterFunctionInputNode : RasterFunctionNode
{
    /// <summary>Name used to bind a typed raster source at invocation time.</summary>
    public required string InputName { get; init; }
}

/// <summary>Passes one raster through unchanged.</summary>
public sealed record RasterFunctionIdentityNode : RasterFunctionNode;

/// <summary>Selects and orders one-based raster bands.</summary>
public sealed record RasterFunctionBandSelectNode : RasterFunctionNode
{
    /// <summary>One-based source band indexes in output order.</summary>
    public required IReadOnlyList<int> Bands { get; init; }
}

/// <summary>Computes one allowlisted two-band spectral index.</summary>
public sealed record RasterFunctionSpectralIndexNode : RasterFunctionNode
{
    /// <summary>The allowlisted spectral formula.</summary>
    public required RasterSpectralIndexMethod Method { get; init; }

    /// <summary>One-based first operand band.</summary>
    public required int PrimaryBand { get; init; }

    /// <summary>One-based second operand band.</summary>
    public required int SecondaryBand { get; init; }
}

/// <summary>Allowlisted spectral-index formulas.</summary>
public enum RasterSpectralIndexMethod
{
    /// <summary>Normalized Difference Vegetation Index.</summary>
    Ndvi = 0,

    /// <summary>Normalized Difference Water Index.</summary>
    Ndwi = 1,

    /// <summary>Soil-Adjusted Vegetation Index with the fixed 0.5 correction factor.</summary>
    Savi = 2,
}

/// <summary>Clips or masks a raster with bounded WKB geometry.</summary>
public sealed record RasterFunctionClipNode : RasterFunctionNode
{
    /// <summary>The provider-neutral clip region.</summary>
    public required RasterClipRegion Region { get; init; }
}

/// <summary>Resamples a raster to explicit dimensions or pixel size.</summary>
public sealed record RasterFunctionResampleNode : RasterFunctionNode
{
    /// <summary>Requested output width; must be paired with <see cref="Height"/>.</summary>
    public int? Width { get; init; }

    /// <summary>Requested output height; must be paired with <see cref="Width"/>.</summary>
    public int? Height { get; init; }

    /// <summary>Requested output pixel size, mutually exclusive with dimensions.</summary>
    public PixelSize? PixelSize { get; init; }

    /// <summary>Allowlisted resampling kernel.</summary>
    public ResamplingAlgorithm Algorithm { get; init; } = ResamplingAlgorithm.NearestNeighbor;
}

/// <summary>Reprojects a raster into one target spatial reference.</summary>
public sealed record RasterFunctionReprojectNode : RasterFunctionNode
{
    /// <summary>Positive target EPSG/SRID identifier.</summary>
    public required int OutputSrid { get; init; }

    /// <summary>Allowlisted resampling kernel used during grid transformation.</summary>
    public ResamplingAlgorithm Algorithm { get; init; } = ResamplingAlgorithm.NearestNeighbor;
}

/// <summary>Applies a typed display stretch.</summary>
public sealed record RasterFunctionStretchNode : RasterFunctionNode
{
    /// <summary>The neutral stretch contract.</summary>
    public required RasterStretch Stretch { get; init; }
}

/// <summary>Applies a typed single-band colormap.</summary>
public sealed record RasterFunctionColormapNode : RasterFunctionNode
{
    /// <summary>The neutral colormap contract.</summary>
    public required RasterColormap Colormap { get; init; }
}

/// <summary>Applies one allowlisted terrain function.</summary>
public sealed record RasterFunctionTerrainNode : RasterFunctionNode
{
    /// <summary>The neutral terrain-function contract.</summary>
    public required RasterTerrainFunction Terrain { get; init; }
}

/// <summary>Reclassifies numeric ranges through a closed rule list.</summary>
public sealed record RasterFunctionReclassifyNode : RasterFunctionNode
{
    /// <summary>Ordered, non-overlapping half-open source ranges.</summary>
    public required IReadOnlyList<RasterReclassificationRule> Rules { get; init; }

    /// <summary>Typed output cell representation.</summary>
    public required RasterFunctionPixelType OutputPixelType { get; init; }

    /// <summary>Optional replacement for source NoData cells; null preserves NoData.</summary>
    public double? NoDataReplacement { get; init; }
}

/// <summary>A half-open raster reclassification range.</summary>
/// <param name="Minimum">Inclusive finite lower bound.</param>
/// <param name="Maximum">Exclusive finite upper bound.</param>
/// <param name="Value">Finite replacement value.</param>
public readonly record struct RasterReclassificationRule(double Minimum, double Maximum, double Value);

/// <summary>Canonical output cell types accepted by typed raster functions.</summary>
public enum RasterFunctionPixelType
{
    /// <summary>Unsigned 8-bit integer.</summary>
    UnsignedByte = 0,

    /// <summary>Signed 8-bit integer.</summary>
    SignedByte = 1,

    /// <summary>Unsigned 16-bit integer.</summary>
    UnsignedShort = 2,

    /// <summary>Signed 16-bit integer.</summary>
    SignedShort = 3,

    /// <summary>Unsigned 32-bit integer.</summary>
    UnsignedInteger = 4,

    /// <summary>Signed 32-bit integer.</summary>
    SignedInteger = 5,

    /// <summary>32-bit floating point.</summary>
    SinglePrecision = 6,

    /// <summary>64-bit floating point.</summary>
    DoublePrecision = 7,
}

/// <summary>Combines two or more aligned raster inputs through an allowlisted reducer.</summary>
public sealed record RasterFunctionCompositeNode : RasterFunctionNode
{
    /// <summary>The deterministic overlap reducer.</summary>
    public required RasterCompositeMethod Method { get; init; }
}

/// <summary>Allowlisted aligned-raster composite reducers.</summary>
public enum RasterCompositeMethod
{
    /// <summary>First non-NoData input wins.</summary>
    First = 0,

    /// <summary>Last non-NoData input wins.</summary>
    Last = 1,

    /// <summary>Minimum non-NoData value wins.</summary>
    Minimum = 2,

    /// <summary>Maximum non-NoData value wins.</summary>
    Maximum = 3,

    /// <summary>Mean of non-NoData values.</summary>
    Mean = 4,
}
