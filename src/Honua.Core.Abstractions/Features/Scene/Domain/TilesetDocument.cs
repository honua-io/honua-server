// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Minimal OGC 3D Tiles 1.1 <c>tileset.json</c> root document model used by
/// the deterministic generation pipeline (#842). Field names match the spec.
/// </summary>
public sealed class TilesetDocument
{
    /// <summary>Asset metadata (spec version).</summary>
    [JsonPropertyName("asset")]
    public TilesetAsset Asset { get; set; } = new();

    /// <summary>Geometric error of the tileset root, in meters.</summary>
    [JsonPropertyName("geometricError")]
    public double GeometricError { get; set; }

    /// <summary>Root tile.</summary>
    [JsonPropertyName("root")]
    public TileNode Root { get; set; } = new();

    /// <summary>
    /// Tileset-level non-normative metadata. v1 uses the <c>extras</c> block to
    /// advertise the attribute-driven 3D symbology style-metadata contract
    /// (<c>honua_style</c>) so a client can discover and fetch the
    /// <c>style.json</c> sidecar without sniffing the GLB.
    /// </summary>
    [JsonPropertyName("extras")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TilesetExtras? Extras { get; set; }
}

/// <summary>
/// Tileset <c>extras</c> block carrying Honua-specific discovery metadata.
/// </summary>
public sealed class TilesetExtras
{
    /// <summary>
    /// Reference to the emitted 3D symbology style-metadata contract.
    /// </summary>
    [JsonPropertyName("honua_style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TilesetStyleReference? Style { get; set; }
}

/// <summary>
/// Pointer the client uses to locate and version the style-metadata contract.
/// </summary>
public sealed class TilesetStyleReference
{
    /// <summary>Encoding discriminator, always <c>3d-tiles-styling</c>.</summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = string.Empty;

    /// <summary>Style-metadata contract version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Relative URI of the style spec sidecar (e.g. <c>style.json</c>).</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

/// <summary>
/// 3D Tiles asset metadata block.
/// </summary>
public sealed class TilesetAsset
{
    /// <summary>Spec version this document conforms to.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.1";

    /// <summary>Optional generator label written into the tileset for traceability.</summary>
    [JsonPropertyName("generator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Generator { get; set; }
}

/// <summary>
/// A single tile node. Either <see cref="Content"/> or <see cref="Children"/>
/// (or both) may be populated.
/// </summary>
public sealed class TileNode
{
    /// <summary>Bounding volume; v1 always emits a region (radians).</summary>
    [JsonPropertyName("boundingVolume")]
    public BoundingVolume BoundingVolume { get; set; } = new();

    /// <summary>Geometric error of this tile, in meters.</summary>
    [JsonPropertyName("geometricError")]
    public double GeometricError { get; set; }

    /// <summary>Refinement strategy. v1 always emits "ADD".</summary>
    [JsonPropertyName("refine")]
    public string Refine { get; set; } = "ADD";

    /// <summary>Optional tile content reference.</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileContent? Content { get; set; }

    /// <summary>Optional child tiles.</summary>
    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TileNode>? Children { get; set; }
}

/// <summary>
/// Tile bounding volume per the OGC 3D Tiles 1.1 specification. Exactly one of
/// <see cref="Region"/>, <see cref="Sphere"/>, or <see cref="Box"/> should be
/// populated. The deterministic generation pipeline emits a WGS-84
/// <see cref="Region"/>; converters that ingest source formats (I3S MBS,
/// gltf/i3dm tiles, etc.) may emit <see cref="Sphere"/> or <see cref="Box"/>
/// instead.
/// </summary>
public sealed class BoundingVolume
{
    /// <summary>WGS-84 region (radians); 6 elements: west, south, east, north, minHeight, maxHeight.</summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Region { get; set; }

    /// <summary>ECEF bounding sphere; 4 elements: centerX, centerY, centerZ (meters), radius (meters).</summary>
    [JsonPropertyName("sphere")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Sphere { get; set; }

    /// <summary>
    /// Oriented bounding box; 12 elements per the OGC 3D Tiles spec
    /// (centerX, centerY, centerZ, halfAxis0X, halfAxis0Y, halfAxis0Z,
    /// halfAxis1X, halfAxis1Y, halfAxis1Z, halfAxis2X, halfAxis2Y, halfAxis2Z).
    /// </summary>
    [JsonPropertyName("box")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Box { get; set; }
}

/// <summary>
/// Reference to a tile's binary content (relative URI under the tileset root).
/// </summary>
public sealed class TileContent
{
    /// <summary>Relative content URI, e.g. <c>tile_0000.glb</c>.</summary>
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
}

/// <summary>
/// Source-generated JSON serialization context for the 3D Tiles tileset.json
/// model. Used by <c>TilesetDocumentWriter</c> for deterministic, AOT-safe
/// serialization without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TilesetDocument))]
[JsonSerializable(typeof(TileNode))]
[JsonSerializable(typeof(BoundingVolume))]
[JsonSerializable(typeof(TileContent))]
[JsonSerializable(typeof(TilesetAsset))]
[JsonSerializable(typeof(TilesetExtras))]
[JsonSerializable(typeof(TilesetStyleReference))]
public sealed partial class TilesetJsonContext : JsonSerializerContext
{
}
