// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// The style-metadata contract emitted alongside a generated 3D Tiles tileset.
/// This is the stable shape the JavaScript client consumes to apply
/// attribute-driven symbology at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The document is a thin, deliberately-stable wrapper around the
/// <see href="https://github.com/CesiumGS/3d-tiles/tree/main/specification/Styling">
/// 3D Tiles Styling expression language</see>. The <see cref="Style"/> block is
/// directly assignable to a CesiumJS <c>Cesium3DTileStyle</c> (its
/// <see cref="TileStyleExpressions.Color"/> and <see cref="TileStyleExpressions.Show"/>
/// strings ARE the styling expressions). The wrapper adds an explicit
/// <see cref="Version"/>, the resolved <see cref="DefaultMaterial"/> the server
/// already baked into the GLB, and the <see cref="Encoding"/> tag so the client
/// can branch on the symbology contract version without sniffing the GLB.
/// </para>
/// <para>
/// Determinism: the document contains no dictionaries; all collections are
/// ordered lists written in authoring order, so serialization is byte-stable
/// across runs given identical input.
/// </para>
/// </remarks>
public sealed class TileStyleSpec
{
    /// <summary>
    /// Encoding discriminator. Always <c>3d-tiles-styling</c> — the recognized
    /// encoding this contract finally gives an engine to.
    /// </summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = TileStyleSpecDefaults.Encoding;

    /// <summary>Contract version. Bumped when the emitted shape changes.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = TileStyleSpecDefaults.Version;

    /// <summary>
    /// The default material the server baked into the tile content. The client
    /// can use this for legends or non-3D-Tiles fallbacks; it is already
    /// reflected in the GLB so a viewer that ignores the style block still
    /// renders the intended base color.
    /// </summary>
    [JsonPropertyName("defaultMaterial")]
    public TileStyleMaterial DefaultMaterial { get; set; } = new();

    /// <summary>
    /// The 3D Tiles Styling expression block. Directly assignable to a
    /// CesiumJS <c>Cesium3DTileStyle</c>.
    /// </summary>
    [JsonPropertyName("style")]
    public TileStyleExpressions Style { get; set; } = new();
}

/// <summary>
/// Resolved default material baked into the generated tile content.
/// </summary>
public sealed class TileStyleMaterial
{
    /// <summary>Base color as a CSS hex literal, e.g. <c>#ffffff</c>.</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = Symbology3DColor.White.ToHex();

    /// <summary>Opacity in [0, 1].</summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;
}

/// <summary>
/// 3D Tiles Styling expression block. The <see cref="Color"/> and
/// <see cref="Show"/> strings are styling expressions (conditions on
/// <c>${attribute}</c>) per the 3D Tiles Styling spec.
/// </summary>
public sealed class TileStyleExpressions
{
    /// <summary>
    /// A <c>color</c> conditions expression. Omitted when no attribute-driven
    /// color rules are configured (the baked GLB color then stands alone).
    /// </summary>
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileStyleConditions? Color { get; set; }

    /// <summary>
    /// A <c>show</c> conditions expression controlling per-feature visibility.
    /// Omitted when no visibility rules are configured.
    /// </summary>
    [JsonPropertyName("show")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileStyleConditions? Show { get; set; }
}

/// <summary>
/// A 3D Tiles Styling <c>conditions</c> expression: an ordered list of
/// [test-expression, result-expression] pairs evaluated top-to-bottom. The
/// shape mirrors the spec's <c>{"conditions": [[cond, value], ...]}</c> form.
/// </summary>
public sealed class TileStyleConditions
{
    /// <summary>
    /// Ordered condition pairs. Each entry is exactly a two-element array:
    /// index 0 is the boolean test expression, index 1 is the result
    /// expression returned for the first matching test.
    /// </summary>
    [JsonPropertyName("conditions")]
    public IReadOnlyList<string[]> Conditions { get; set; } = [];
}

/// <summary>Stable defaults for the style-metadata contract.</summary>
public static class TileStyleSpecDefaults
{
    /// <summary>Recognized encoding discriminator for the 3D symbology contract.</summary>
    public const string Encoding = "3d-tiles-styling";

    /// <summary>Current contract version.</summary>
    public const string Version = "1.0";

    /// <summary>Conventional file name the generator writes the spec to.</summary>
    public const string FileName = "style.json";
}

/// <summary>
/// Source-generated JSON serialization context for the 3D symbology
/// style-metadata contract. Deterministic, AOT-safe, no reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TileStyleSpec))]
[JsonSerializable(typeof(TileStyleMaterial))]
[JsonSerializable(typeof(TileStyleExpressions))]
[JsonSerializable(typeof(TileStyleConditions))]
public sealed partial class TileStyleSpecJsonContext : JsonSerializerContext
{
}
