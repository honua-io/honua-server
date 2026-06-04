// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Produces a deterministic GLB (binary glTF 2.0) blob containing the
/// projected geometry and per-feature attribute metadata for one tile of
/// the v1 3D Tiles generation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The writer materializes a single mesh primitive whose mode is dictated by
/// the homogeneous geometry kind of the input features (POINTS for points,
/// LINES for line strings, TRIANGLES for polygons / extruded prisms). Vertex
/// positions are encoded in ECEF meters (EPSG:4978) so CesiumJS can place the
/// geometry on the ellipsoid without further client-side transforms.
/// </para>
/// <para>
/// Per-vertex feature ids and the per-feature attribute table are emitted
/// through the <c>EXT_mesh_features</c> and <c>EXT_structural_metadata</c>
/// glTF extensions so identify flows can attribute hits back to the source
/// feature row. The metadata table is serialized in a stable column order
/// (input order), which combined with deterministic vertex traversal gives
/// byte-identical output across runs against the same source data.
/// </para>
/// </remarks>
public static class GeometryTileBuilder
{
    private const int GlbHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint GlbVersion = 2;

    /// <summary>
    /// Largest feature count whose per-feature integer id is representable
    /// exactly as a 32-bit float (2^24). The <c>_FEATURE_ID_0</c> accessor and
    /// the COLOR_0 baking carry the per-vertex feature index through a float
    /// buffer; beyond this ceiling distinct ids would alias, corrupting both the
    /// structural-metadata attribution and the symbology lookup. Production tile
    /// caps sit far below this, but the guard makes the limit explicit rather
    /// than implicit so an uncapped caller fails loudly instead of silently.
    /// </summary>
    private const int MaxExactFloatFeatureCount = 1 << 24;
    private const uint JsonChunkType = 0x4E4F534A; // "JSON"
    private const uint BinChunkType = 0x004E4942; // "BIN\0"

    /// <summary>
    /// Builds a single-primitive GLB tile from the supplied features.
    /// </summary>
    /// <param name="features">Ordered, homogeneous-geometry features. Must be non-empty.</param>
    /// <param name="metadataAttributes">Ordered list of attribute schema definitions to emit.</param>
    /// <param name="extrusion">Optional extrusion that converts polygons into vertical prisms.</param>
    /// <param name="generatorTag">Optional generator label (omitted when null/empty).</param>
    /// <param name="warnings">Optional sink for non-fatal coercion warnings (e.g., int64 → int32 clamping).</param>
    /// <param name="perFeatureSymbology">
    /// Optional resolved per-feature symbology, indexed parallel to
    /// <paramref name="features"/>. When supplied, the builder bakes the
    /// resolved RGBA color into a normalized <c>COLOR_0</c> vertex attribute and
    /// switches the primitive material to a metallic-roughness material with a
    /// neutral <c>baseColorFactor</c> so per-vertex colors multiply through.
    /// When null the builder emits the legacy single double-sided white
    /// material with no vertex colors.
    /// </param>
    /// <returns>A deterministic GLB byte sequence.</returns>
    public static byte[] BuildGlb(
        IReadOnlyList<SceneFeature> features,
        IReadOnlyList<SceneAttributeSchema> metadataAttributes,
        MetadataV2ExtrusionInfo? extrusion,
        string? generatorTag = null,
        IList<string>? warnings = null,
        IReadOnlyList<ResolvedSymbology>? perFeatureSymbology = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(metadataAttributes);

        if (features.Count == 0)
        {
            throw new ArgumentException("At least one feature is required to build a tile.", nameof(features));
        }

        if (features.Count > MaxExactFloatFeatureCount)
        {
            // The per-vertex feature id is carried through a float32 buffer (the
            // _FEATURE_ID_0 accessor and the COLOR_0 baking lookup). Past 2^24
            // distinct integer ids alias, so refuse to emit a corrupt tile and
            // require the caller to split the feature set instead.
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "A single tile cannot carry more than {0} features; split the feature set across tiles.",
                    MaxExactFloatFeatureCount),
                nameof(features));
        }

        if (perFeatureSymbology is not null && perFeatureSymbology.Count != features.Count)
        {
            throw new ArgumentException(
                "perFeatureSymbology must have exactly one entry per feature.",
                nameof(perFeatureSymbology));
        }

        var kind = features[0].Geometry.Kind;
        for (var i = 1; i < features.Count; i++)
        {
            if (features[i].Geometry.Kind != kind)
            {
                throw new ArgumentException(
                    "All features in a tile must share the same geometry kind.",
                    nameof(features));
            }
        }

        // 1. Build geometry buffers (positions + per-vertex feature ids).
        // Positions are accumulated in double-precision ECEF meters so the RTC
        // centroid (computed below) can be subtracted BEFORE the float cast; a
        // direct float cast of absolute ECEF would already have lost ~1 m before
        // any recentering could help.
        var positions = new List<double>(features.Count * 8);
        var featureIds = new List<float>(features.Count * 8);
        var primitiveMode = kind switch
        {
            SceneGeometryKind.Point => 0, // POINTS
            SceneGeometryKind.LineString => 1, // LINES
            SceneGeometryKind.Polygon => 4, // TRIANGLES
            _ => throw new InvalidOperationException(
                $"Unsupported scene geometry kind '{kind}'.")
        };

        for (var i = 0; i < features.Count; i++)
        {
            AppendFeature(features[i], (float)i, kind, extrusion, positions, featureIds);
        }

        var vertexCount = positions.Count / 3;
        if (vertexCount == 0)
        {
            throw new InvalidOperationException(
                $"No renderable vertices produced for tile: all {features.Count} feature(s) of kind " +
                $"'{kind}' are degenerate (e.g. LineString with <2 vertices, empty Point, or Polygon ring " +
                "with <3 distinct vertices). Callers should drop such tiles before BuildGlb rather than " +
                "routing a vertex-less feature set into it.");
        }

        // 1b. Recenter positions about the tile's ECEF centroid (RTC). Vertex
        // positions are absolute ECEF meters (magnitude ~6.3e6 near the
        // surface); a float32 has ~24 bits of mantissa, so storing absolute
        // ECEF quantizes every vertex to ~0.5-1 m. Subtract a per-tile centroid
        // (accumulated in double precision in AppendVertex) BEFORE the float
        // cast and emit the centroid as the glTF node `translation`, so the
        // float positions stay small-magnitude and retain sub-centimeter
        // precision. This mirrors the PNTS path's RTC_CENTER (PntsTileWriter).
        // The centroid is the arithmetic mean of the double-precision vertex
        // positions, so it is deterministic for identical input.
        var translation = ComputeCentroid(positions, vertexCount);
        var positionFloats = RecenterToFloat(positions, translation);

        // 2. Build the per-feature property table buffers.
        var propertyTable = SceneMetadataTable.Build(features, metadataAttributes, warnings);

        // 2b. Bake per-vertex colors from the resolved symbology (if any). The
        // color for vertex v is the resolved color of the feature that owns v;
        // featureIds already carries that ownership in vertex order, so the
        // color buffer stays in lockstep with positions deterministically.
        byte[]? colorBytes = null;
        if (perFeatureSymbology is not null)
        {
            colorBytes = new byte[vertexCount * 4];
            for (var v = 0; v < vertexCount; v++)
            {
                var symbology = perFeatureSymbology[(int)featureIds[v]];
                var alpha = (byte)Math.Clamp((int)Math.Round(symbology.Opacity * 255.0), 0, 255);
                colorBytes[v * 4] = symbology.Color.Red;
                colorBytes[v * 4 + 1] = symbology.Color.Green;
                colorBytes[v * 4 + 2] = symbology.Color.Blue;
                colorBytes[v * 4 + 3] = alpha;
            }
        }

        // 3. Lay out the binary buffer: positions, feature ids, optional vertex
        // colors, then each property column.
        var positionBytes = FloatsToByteArray(positionFloats);
        var featureIdBytes = AsByteSpan(featureIds);

        var bufferViews = new List<BufferViewDescriptor>(3 + propertyTable.Columns.Count);

        // Pre-size the binary buffer to the exact payload total plus per-append
        // alignment slack, so the backing array is allocated once instead of
        // repeatedly doubling onto the LOH. Output bytes are unchanged (the
        // alignment math below is unaffected); this only reserves capacity.
        var estimatedCapacity = positionBytes.Length + featureIdBytes.Length + (colorBytes?.Length ?? 0);
        foreach (var column in propertyTable.Columns)
        {
            estimatedCapacity += column.Bytes.Length + (column.StringOffsetBytes?.Length ?? 0);
        }

        // Reserve worst-case alignment padding: positions/feature-ids/colors are
        // 4-aligned and each property column (plus its optional string-offset
        // view) can pad up to its component alignment; budget a fixed-8 pad per
        // appended view to stay an upper bound regardless of alignment.
        estimatedCapacity += 8 * (3 + (propertyTable.Columns.Count * 2));
        var binaryWriter = new BinaryBufferBuilder(estimatedCapacity);

        var positionView = binaryWriter.Append(positionBytes);
        bufferViews.Add(new BufferViewDescriptor(positionView, BufferViewTarget.ArrayBuffer));

        var featureIdView = binaryWriter.Append(featureIdBytes);
        bufferViews.Add(new BufferViewDescriptor(featureIdView, BufferViewTarget.ArrayBuffer));

        var colorViewIndex = -1;
        if (colorBytes is not null)
        {
            var colorView = binaryWriter.Append(colorBytes);
            colorViewIndex = bufferViews.Count;
            bufferViews.Add(new BufferViewDescriptor(colorView, BufferViewTarget.ArrayBuffer));
        }

        var propertyViewIndices = new int[propertyTable.Columns.Count];
        for (var i = 0; i < propertyTable.Columns.Count; i++)
        {
            var column = propertyTable.Columns[i];
            var view = binaryWriter.Append(column.Bytes, column.ByteAlignment);
            propertyViewIndices[i] = bufferViews.Count;
            bufferViews.Add(new BufferViewDescriptor(view, BufferViewTarget.None));

            if (column.StringOffsetBytes is { } offsetBytes)
            {
                var offsetView = binaryWriter.Append(offsetBytes);
                column.StringOffsetBufferView = bufferViews.Count;
                bufferViews.Add(new BufferViewDescriptor(offsetView, BufferViewTarget.None));
            }
        }

        var binaryPayload = binaryWriter.GetPayloadWithPadding();

        // 4. Compose JSON document.
        var jsonBytes = BuildJsonChunk(
            primitiveMode,
            vertexCount,
            positionFloats,
            translation,
            bufferViews,
            positionViewIndex: 0,
            featureIdViewIndex: 1,
            colorViewIndex,
            featureCount: features.Count,
            propertyTable,
            propertyViewIndices,
            binaryPayload.Length,
            generatorTag);

        var paddedJson = PadJsonToFourBytes(jsonBytes);

        // 5. Assemble GLB.
        var totalLength = GlbHeaderLength
            + ChunkHeaderLength + paddedJson.Length
            + ChunkHeaderLength + binaryPayload.Length;

        var glb = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)totalLength);

        var offset = GlbHeaderLength;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset, 4), (uint)paddedJson.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset + 4, 4), JsonChunkType);
        offset += ChunkHeaderLength;
        paddedJson.CopyTo(glb.AsSpan(offset, paddedJson.Length));
        offset += paddedJson.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset, 4), (uint)binaryPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(offset + 4, 4), BinChunkType);
        offset += ChunkHeaderLength;
        binaryPayload.CopyTo(glb.AsSpan(offset, binaryPayload.Length));

        return glb;
    }

    private static void AppendFeature(
        SceneFeature feature,
        float featureIndex,
        SceneGeometryKind kind,
        MetadataV2ExtrusionInfo? extrusion,
        List<double> positions,
        List<float> featureIds)
    {
        var vertices = feature.Geometry.Vertices;
        switch (kind)
        {
            case SceneGeometryKind.Point:
                if (vertices.Count == 0)
                {
                    return;
                }

                AppendVertex(vertices[0], heightOverride: null, positions, featureIds, featureIndex);
                break;

            case SceneGeometryKind.LineString:
                for (var i = 0; i < vertices.Count - 1; i++)
                {
                    AppendVertex(vertices[i], heightOverride: null, positions, featureIds, featureIndex);
                    AppendVertex(vertices[i + 1], heightOverride: null, positions, featureIds, featureIndex);
                }
                break;

            case SceneGeometryKind.Polygon:
                AppendPolygonTriangles(feature, featureIndex, extrusion, positions, featureIds);
                break;
        }
    }

    private static void AppendVertex(
        SceneVertex vertex,
        double? heightOverride,
        List<double> positions,
        List<float> featureIds,
        float featureIndex)
    {
        var height = heightOverride ?? vertex.Height ?? 0.0;
        var (x, y, z) = EcefCoordinateTransform.ToEcef(vertex.Longitude, vertex.Latitude, height);
        positions.Add(x);
        positions.Add(y);
        positions.Add(z);
        featureIds.Add(featureIndex);
    }

    private static void AppendPolygonTriangles(
        SceneFeature feature,
        float featureIndex,
        MetadataV2ExtrusionInfo? extrusion,
        List<double> positions,
        List<float> featureIds)
    {
        var ring = feature.Geometry.Vertices;
        if (ring.Count < 3)
        {
            return;
        }

        // v1 uses a fan triangulation rooted at the first vertex; correct for
        // convex rings and deterministic for non-convex inputs (rendering may
        // be visually imperfect — documented limitation).
        var ringCount = ring.Count;
        // If the ring is closed (first == last), drop the duplicate.
        if (ring[0].Longitude == ring[ringCount - 1].Longitude
            && ring[0].Latitude == ring[ringCount - 1].Latitude
            && (ring[0].Height ?? 0.0) == (ring[ringCount - 1].Height ?? 0.0))
        {
            ringCount--;
        }
        if (ringCount < 3)
        {
            return;
        }

        var hasExtrusion = extrusion is not null;
        var topHeight = hasExtrusion
            ? SceneExtrusionResolver.ResolveTopHeightMeters(feature, extrusion!)
            : 0.0;

        if (!hasExtrusion)
        {
            // Flat polygon: triangulate top face only with feature vertices' Z.
            for (var i = 1; i < ringCount - 1; i++)
            {
                AppendVertex(ring[0], heightOverride: null, positions, featureIds, featureIndex);
                AppendVertex(ring[i], heightOverride: null, positions, featureIds, featureIndex);
                AppendVertex(ring[i + 1], heightOverride: null, positions, featureIds, featureIndex);
            }
            return;
        }

        // Extruded prism: top face, bottom face (reversed winding), and walls.
        var baseHeight = SceneExtrusionResolver.ResolveBaseHeightMeters(feature, extrusion!);
        var topZ = baseHeight + topHeight;

        for (var i = 1; i < ringCount - 1; i++)
        {
            AppendVertex(ring[0], topZ, positions, featureIds, featureIndex);
            AppendVertex(ring[i], topZ, positions, featureIds, featureIndex);
            AppendVertex(ring[i + 1], topZ, positions, featureIds, featureIndex);
        }
        for (var i = 1; i < ringCount - 1; i++)
        {
            AppendVertex(ring[0], baseHeight, positions, featureIds, featureIndex);
            AppendVertex(ring[i + 1], baseHeight, positions, featureIds, featureIndex);
            AppendVertex(ring[i], baseHeight, positions, featureIds, featureIndex);
        }
        for (var i = 0; i < ringCount; i++)
        {
            var current = ring[i];
            var next = ring[(i + 1) % ringCount];
            AppendVertex(current, baseHeight, positions, featureIds, featureIndex);
            AppendVertex(next, baseHeight, positions, featureIds, featureIndex);
            AppendVertex(next, topZ, positions, featureIds, featureIndex);

            AppendVertex(current, baseHeight, positions, featureIds, featureIndex);
            AppendVertex(next, topZ, positions, featureIds, featureIndex);
            AppendVertex(current, topZ, positions, featureIds, featureIndex);
        }
    }

    private static byte[] AsByteSpan(List<float> values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    private static byte[] FloatsToByteArray(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    /// <summary>
    /// Computes the RTC centroid (arithmetic mean of the double-precision ECEF
    /// vertex positions). Returned as a double[3] so it can be emitted verbatim
    /// as the glTF node <c>translation</c> without a lossy float cast.
    /// </summary>
    private static double[] ComputeCentroid(List<double> positions, int vertexCount)
    {
        double cx = 0, cy = 0, cz = 0;
        for (var i = 0; i < positions.Count; i += 3)
        {
            cx += positions[i];
            cy += positions[i + 1];
            cz += positions[i + 2];
        }
        return new[] { cx / vertexCount, cy / vertexCount, cz / vertexCount };
    }

    /// <summary>
    /// Subtracts the RTC centroid from each double-precision position and casts
    /// to float32. Recentering before the cast keeps the magnitudes small so the
    /// float mantissa retains sub-centimeter precision; the absolute position is
    /// reconstructed client-side as <c>translation + position</c>.
    /// </summary>
    private static float[] RecenterToFloat(List<double> positions, double[] translation)
    {
        var result = new float[positions.Count];
        for (var i = 0; i < positions.Count; i += 3)
        {
            result[i] = (float)(positions[i] - translation[0]);
            result[i + 1] = (float)(positions[i + 1] - translation[1]);
            result[i + 2] = (float)(positions[i + 2] - translation[2]);
        }
        return result;
    }

    private static byte[] PadJsonToFourBytes(byte[] json)
    {
        var padding = (4 - (json.Length & 3)) & 3;
        if (padding == 0)
        {
            return json;
        }
        var padded = new byte[json.Length + padding];
        json.CopyTo(padded, 0);
        for (var i = 0; i < padding; i++)
        {
            padded[json.Length + i] = 0x20;
        }
        return padded;
    }

    private static byte[] BuildJsonChunk(
        int primitiveMode,
        int vertexCount,
        float[] positions,
        double[] translation,
        List<BufferViewDescriptor> bufferViews,
        int positionViewIndex,
        int featureIdViewIndex,
        int colorViewIndex,
        int featureCount,
        SceneMetadataTable propertyTable,
        int[] propertyViewIndices,
        int totalBinaryLength,
        string? generatorTag)
    {
        var hasVertexColors = colorViewIndex >= 0;
        var (minX, minY, minZ, maxX, maxY, maxZ) = ComputePositionBounds(positions);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("asset");
            writer.WriteStartObject();
            writer.WriteString("version", "2.0");
            if (!string.IsNullOrEmpty(generatorTag))
            {
                writer.WriteString("generator", generatorTag);
            }
            writer.WriteEndObject();

            writer.WriteStartArray("extensionsUsed");
            writer.WriteStringValue("EXT_mesh_features");
            writer.WriteStringValue("EXT_structural_metadata");
            writer.WriteEndArray();

            writer.WriteStartArray("scenes");
            writer.WriteStartObject();
            writer.WriteStartArray("nodes");
            writer.WriteNumberValue(0);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteNumber("scene", 0);

            writer.WriteStartArray("nodes");
            writer.WriteStartObject();
            writer.WriteNumber("mesh", 0);
            // RTC translation: the absolute ECEF centroid subtracted from every
            // vertex above. Emitting it as the node translation lets the client
            // reconstruct absolute ECEF (translation + position) while the stored
            // float positions stay small-magnitude and precise. This replaces the
            // former bare {"mesh":0} node (which left absolute ECEF in float32).
            writer.WriteStartArray("translation");
            writer.WriteNumberValue(translation[0]);
            writer.WriteNumberValue(translation[1]);
            writer.WriteNumberValue(translation[2]);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("meshes");
            writer.WriteStartObject();
            writer.WriteStartArray("primitives");
            writer.WriteStartObject();

            writer.WriteStartObject("attributes");
            writer.WriteNumber("POSITION", 0);
            writer.WriteNumber("_FEATURE_ID_0", 1);
            if (hasVertexColors)
            {
                // Accessor 2 holds the baked normalized RGBA vertex colors.
                writer.WriteNumber("COLOR_0", 2);
            }
            writer.WriteEndObject();

            writer.WriteNumber("mode", primitiveMode);
            // Reference the doubleSided default material so triangle-mode
            // primitives are not culled by glTF's CCW-front-face rule when
            // source rings ship with CW winding. The material has no
            // visual effect on POINTS/LINES modes; emitting it
            // unconditionally keeps the GLB self-consistent across kinds.
            writer.WriteNumber("material", 0);

            writer.WriteStartObject("extensions");
            writer.WriteStartObject("EXT_mesh_features");
            writer.WriteStartArray("featureIds");
            writer.WriteStartObject();
            writer.WriteNumber("featureCount", featureCount);
            writer.WriteNumber("attribute", 0);
            writer.WriteNumber("propertyTable", 0);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("accessors");
            writer.WriteStartObject();
            writer.WriteNumber("bufferView", positionViewIndex);
            writer.WriteNumber("componentType", 5126);
            writer.WriteNumber("count", vertexCount);
            writer.WriteString("type", "VEC3");
            writer.WriteStartArray("min");
            writer.WriteNumberValue(minX);
            writer.WriteNumberValue(minY);
            writer.WriteNumberValue(minZ);
            writer.WriteEndArray();
            writer.WriteStartArray("max");
            writer.WriteNumberValue(maxX);
            writer.WriteNumberValue(maxY);
            writer.WriteNumberValue(maxZ);
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteNumber("bufferView", featureIdViewIndex);
            writer.WriteNumber("componentType", 5126);
            writer.WriteNumber("count", vertexCount);
            writer.WriteString("type", "SCALAR");
            writer.WriteEndObject();

            if (hasVertexColors)
            {
                // COLOR_0 accessor: normalized unsigned-byte RGBA. glTF readers
                // expand a VEC4 UBYTE accessor with normalized=true to [0,1]
                // floats, so the baked 0-255 channels render at full fidelity.
                writer.WriteStartObject();
                writer.WriteNumber("bufferView", colorViewIndex);
                writer.WriteNumber("componentType", 5121); // UNSIGNED_BYTE
                writer.WriteBoolean("normalized", true);
                writer.WriteNumber("count", vertexCount);
                writer.WriteString("type", "VEC4");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Default double-sided material — referenced by `material: 0`
            // on every primitive. glTF 2.0 §3.6.4 says doubleSided=false is
            // the default; without an override, viewers cull triangles that
            // face away from the camera. Source PostGIS polygon rings can
            // be CW or CCW, so a single double-sided material is the
            // smallest v1 fix that keeps every viewer (Cesium, three.js,
            // any OpenGL-derived stack) honest about both faces.
            writer.WriteStartArray("materials");
            writer.WriteStartObject();
            writer.WriteString("name", "honua_default");
            writer.WriteBoolean("doubleSided", true);
            if (hasVertexColors)
            {
                // Per-vertex COLOR_0 multiplies against baseColorFactor in the
                // glTF PBR model. A neutral white, fully-rough, non-metal base
                // lets the baked symbology color pass through unaltered while
                // staying a valid metallic-roughness material. alphaMode BLEND
                // honors the per-vertex alpha channel (resolved opacity).
                writer.WriteStartObject("pbrMetallicRoughness");
                writer.WriteStartArray("baseColorFactor");
                writer.WriteNumberValue(1.0);
                writer.WriteNumberValue(1.0);
                writer.WriteNumberValue(1.0);
                writer.WriteNumberValue(1.0);
                writer.WriteEndArray();
                writer.WriteNumber("metallicFactor", 0.0);
                writer.WriteNumber("roughnessFactor", 1.0);
                writer.WriteEndObject();
                writer.WriteString("alphaMode", "BLEND");
            }
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("bufferViews");
            foreach (var view in bufferViews)
            {
                writer.WriteStartObject();
                writer.WriteNumber("buffer", 0);
                writer.WriteNumber("byteOffset", view.ByteOffset);
                writer.WriteNumber("byteLength", view.ByteLength);
                if (view.Target != BufferViewTarget.None)
                {
                    writer.WriteNumber("target", (int)view.Target);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("buffers");
            writer.WriteStartObject();
            writer.WriteNumber("byteLength", totalBinaryLength);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartObject("extensions");
            writer.WriteStartObject("EXT_structural_metadata");
            WriteSchema(writer, propertyTable);

            writer.WriteStartArray("propertyTables");
            writer.WriteStartObject();
            writer.WriteString("class", SceneMetadataTable.SchemaClassName);
            writer.WriteNumber("count", featureCount);
            writer.WriteStartObject("properties");
            for (var i = 0; i < propertyTable.Columns.Count; i++)
            {
                var column = propertyTable.Columns[i];
                writer.WriteStartObject(column.PropertyId);
                writer.WriteNumber("values", propertyViewIndices[i]);
                if (column.StringOffsetBufferView is { } offsetView)
                {
                    writer.WriteNumber("stringOffsets", offsetView);
                    writer.WriteString("stringOffsetType", "UINT32");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static (double, double, double, double, double, double) ComputePositionBounds(float[] positions)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;

        for (var i = 0; i < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static void WriteSchema(Utf8JsonWriter writer, SceneMetadataTable table)
    {
        writer.WriteStartObject("schema");
        writer.WriteString("id", "honua_scene_schema");
        writer.WriteStartObject("classes");
        writer.WriteStartObject(SceneMetadataTable.SchemaClassName);
        writer.WriteString("name", SceneMetadataTable.SchemaClassName);
        writer.WriteStartObject("properties");
        foreach (var column in table.Columns)
        {
            writer.WriteStartObject(column.PropertyId);
            writer.WriteString("type", column.SchemaType);
            if (!string.IsNullOrEmpty(column.SchemaComponentType))
            {
                writer.WriteString("componentType", column.SchemaComponentType);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private enum BufferViewTarget
    {
        None = 0,
        ArrayBuffer = 34962
    }

    private readonly record struct BufferViewDescriptor(BufferRange Range, BufferViewTarget Target)
    {
        public int ByteOffset => Range.ByteOffset;
        public int ByteLength => Range.ByteLength;
    }

    private readonly record struct BufferRange(int ByteOffset, int ByteLength);

    private sealed class BinaryBufferBuilder
    {
        // Single pre-sized backing array. Each Append copies its payload exactly
        // once (the prior List<byte>-backed implementation copied every payload
        // three times: AsByteSpan -> AddRange -> ToArray). The estimated
        // capacity passed by BuildGlb is an upper bound that includes worst-case
        // alignment slack, so _buffer never reallocates; GetPayloadWithPadding
        // returns the exact-length slice (zero-trailing-pad included).
        private readonly byte[] _buffer;
        private int _length;

        public BinaryBufferBuilder(int capacity)
        {
            // capacity is a guaranteed upper bound from BuildGlb. Allocate once.
            _buffer = new byte[capacity > 0 ? capacity : 0];
        }

        public BufferRange Append(byte[] payload, int alignment = 4)
        {
            // glTF 2.0 requires bufferView byteOffset to be aligned to the
            // accessor's component size (4 bytes for FLOAT/UINT32).
            // EXT_structural_metadata extends this for property-table values,
            // requiring alignment equal to the component byte length (8 for
            // INT64/UINT64). Pad with zeros to keep alignment deterministic.
            var mask = alignment - 1;
            var pad = (alignment - (_length & mask)) & mask;
            // _buffer is zero-initialized so advancing past the pad bytes leaves
            // them at 0 without an explicit write.
            _length += pad;
            var offset = _length;
            payload.CopyTo(_buffer.AsSpan(_length));
            _length += payload.Length;
            return new BufferRange(offset, payload.Length);
        }

        public byte[] GetPayloadWithPadding()
        {
            var pad = (4 - (_length & 3)) & 3;
            _length += pad;
            // One final copy to the exact tile length. This is unavoidable: the
            // GLB assembler needs a byte[] of the precise padded length to splice
            // into the BIN chunk, and the backing array is an upper-bound size.
            if (_length == _buffer.Length)
            {
                return _buffer;
            }
            return _buffer.AsSpan(0, _length).ToArray();
        }
    }
}

/// <summary>
/// Schema entry for one attribute column emitted into the GLB metadata table.
/// </summary>
public sealed record SceneAttributeSchema
{
    /// <summary>Property id surfaced in EXT_structural_metadata (sanitized field name).</summary>
    public required string PropertyId { get; init; }

    /// <summary>Field name as it appears on the source feature.</summary>
    public required string FieldName { get; init; }

    /// <summary>Schema-level type (e.g. SCALAR, STRING).</summary>
    public required string SchemaType { get; init; }

    /// <summary>Schema component type (e.g. FLOAT32, INT32). Empty for STRING.</summary>
    public string SchemaComponentType { get; init; } = string.Empty;
}

internal sealed class SceneMetadataTable
{
    internal const string SchemaClassName = "honua_feature_class";

    internal IReadOnlyList<SceneMetadataColumn> Columns { get; }

    private SceneMetadataTable(IReadOnlyList<SceneMetadataColumn> columns)
    {
        Columns = columns;
    }

    internal static SceneMetadataTable Build(
        IReadOnlyList<SceneFeature> features,
        IReadOnlyList<SceneAttributeSchema> attributes,
        IList<string>? warnings = null)
    {
        var columns = new List<SceneMetadataColumn>(attributes.Count);
        foreach (var attribute in attributes)
        {
            columns.Add(SceneMetadataColumn.Build(attribute, features, warnings));
        }
        return new SceneMetadataTable(columns);
    }
}

internal sealed class SceneMetadataColumn
{
    public required string PropertyId { get; init; }
    public required string SchemaType { get; init; }
    public required string SchemaComponentType { get; init; }
    public required byte[] Bytes { get; init; }
    public byte[]? StringOffsetBytes { get; init; }
    public int? StringOffsetBufferView { get; set; }

    /// <summary>
    /// Required bufferView byteOffset alignment for the values bytes.
    /// EXT_structural_metadata mandates alignment equal to the component
    /// byte length (8 for INT64/UINT64, 4 for INT32/FLOAT32, 1 for STRING).
    /// </summary>
    public int ByteAlignment { get; init; } = 4;

    public static SceneMetadataColumn Build(
        SceneAttributeSchema schema,
        IReadOnlyList<SceneFeature> features,
        IList<string>? warnings = null)
    {
        return schema.SchemaType switch
        {
            "SCALAR" when schema.SchemaComponentType == "FLOAT32" => BuildScalarFloat(schema, features),
            "SCALAR" when schema.SchemaComponentType == "INT32" => BuildScalarInt32(schema, features, warnings),
            "SCALAR" when schema.SchemaComponentType == "INT64" => BuildScalarInt64(schema, features, warnings),
            "STRING" => BuildString(schema, features),
            _ => throw new InvalidOperationException(
                $"Unsupported attribute schema {schema.SchemaType}/{schema.SchemaComponentType}")
        };
    }

    private static SceneMetadataColumn BuildScalarFloat(SceneAttributeSchema schema, IReadOnlyList<SceneFeature> features)
    {
        var bytes = new byte[features.Count * 4];
        for (var i = 0; i < features.Count; i++)
        {
            features[i].Attributes.TryGetValue(schema.FieldName, out var value);
            var f = ToFloat(value);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), f);
        }
        return new SceneMetadataColumn
        {
            PropertyId = schema.PropertyId,
            SchemaType = "SCALAR",
            SchemaComponentType = "FLOAT32",
            Bytes = bytes
        };
    }

    private static SceneMetadataColumn BuildScalarInt32(
        SceneAttributeSchema schema,
        IReadOnlyList<SceneFeature> features,
        IList<string>? warnings)
    {
        var bytes = new byte[features.Count * 4];
        var clamped = 0;
        for (var i = 0; i < features.Count; i++)
        {
            features[i].Attributes.TryGetValue(schema.FieldName, out var value);
            var v = ToInt32(value, ref clamped);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), v);
        }
        if (clamped > 0 && warnings is not null)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Attribute '{0}' clamped {1} value(s) to INT32 range; widen the schema mapping if exact values are required.",
                schema.FieldName,
                clamped));
        }
        return new SceneMetadataColumn
        {
            PropertyId = schema.PropertyId,
            SchemaType = "SCALAR",
            SchemaComponentType = "INT32",
            Bytes = bytes
        };
    }

    private static SceneMetadataColumn BuildScalarInt64(
        SceneAttributeSchema schema,
        IReadOnlyList<SceneFeature> features,
        IList<string>? warnings)
    {
        var bytes = new byte[features.Count * 8];
        var coerced = 0;
        for (var i = 0; i < features.Count; i++)
        {
            features[i].Attributes.TryGetValue(schema.FieldName, out var value);
            var v = ToInt64(value, ref coerced);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8, 8), v);
        }
        if (coerced > 0 && warnings is not null)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Attribute '{0}' coerced {1} non-integral value(s) to INT64; check the source layer for unexpected types.",
                schema.FieldName,
                coerced));
        }
        return new SceneMetadataColumn
        {
            PropertyId = schema.PropertyId,
            SchemaType = "SCALAR",
            SchemaComponentType = "INT64",
            Bytes = bytes,
            ByteAlignment = 8
        };
    }

    private static SceneMetadataColumn BuildString(SceneAttributeSchema schema, IReadOnlyList<SceneFeature> features)
    {
        // Two-pass build: encode each value once into a reusable per-feature byte
        // buffer to compute offsets/total length, then allocate the values array
        // exactly once and write each segment into its slice. This avoids the
        // List<byte> grow-by-doubling churn AND the final ToArray copy the prior
        // AddRange+ToArray approach paid, mirroring the single-allocation pattern
        // BuildGlb already uses for its binary buffers. Output bytes are identical.
        var encoded = new byte[features.Count][];
        var offsets = new uint[features.Count + 1];
        offsets[0] = 0;
        var total = 0;
        for (var i = 0; i < features.Count; i++)
        {
            features[i].Attributes.TryGetValue(schema.FieldName, out var raw);
            // Format with the invariant culture so byte output is locale-stable
            // (the class-level byte-identical guarantee). For plain strings this
            // is a no-op; for an IFormattable (numeric/date/decimal value routed
            // to a STRING column) it pins invariant formatting instead of the
            // thread culture's (e.g. "1234.5" not "1234,5").
            encoded[i] = Encoding.UTF8.GetBytes(FormatStringValue(raw));
            total += encoded[i].Length;
            offsets[i + 1] = (uint)total;
        }

        // glTF 2.0 requires bufferView.byteLength >= 1, so a column whose
        // values are all null/empty would otherwise emit an invalid zero-byte
        // values view. Pad with a single 0x00 byte; every offset stays at 0
        // and every string therefore decodes as empty (length = next - prev).
        byte[] values;
        if (total == 0)
        {
            values = new byte[] { 0 };
        }
        else
        {
            values = new byte[total];
            var written = 0;
            for (var i = 0; i < encoded.Length; i++)
            {
                var segment = encoded[i];
                Buffer.BlockCopy(segment, 0, values, written, segment.Length);
                written += segment.Length;
            }
        }

        var offsetBytes = new byte[offsets.Length * 4];
        for (var i = 0; i < offsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(offsetBytes.AsSpan(i * 4, 4), offsets[i]);
        }

        return new SceneMetadataColumn
        {
            PropertyId = schema.PropertyId,
            SchemaType = "STRING",
            SchemaComponentType = string.Empty,
            Bytes = values,
            StringOffsetBytes = offsetBytes
        };
    }

    private static string FormatStringValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static float ToFloat(object? value) => value switch
    {
        null => 0f,
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        short s => s,
        decimal m => (float)m,
        string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0f
    };

    private static int ToInt32(object? value, ref int clampedCount)
    {
        switch (value)
        {
            case null: return 0;
            case int i: return i;
            case long l:
                if (l < int.MinValue || l > int.MaxValue)
                {
                    clampedCount++;
                    return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
                }
                return (int)l;
            case short s: return s;
            case float f:
                return ClampFiniteFloatingToInt32(f, ref clampedCount);
            case double d:
                return ClampFiniteFloatingToInt32(d, ref clampedCount);
            case decimal m:
                return ClampDecimalToInt32(m, ref clampedCount);
            case string s:
                return ParseStringToInt32(s, ref clampedCount);
            default: return 0;
        }
    }

    private static int ParseStringToInt32(string s, ref int clampedCount)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        // Postgres scene feature source reads JSONB attributes through
        // `attributes ->> @field` which always returns TEXT. A native
        // bigint or numeric value lands here as the text "2147483648" or
        // "9999999999.5" — the documented INT32 clamping warning has to
        // fire, otherwise out-of-range numeric strings silently flatten to
        // 0 with no operator signal.
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            if (l < int.MinValue || l > int.MaxValue)
            {
                clampedCount++;
                return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            }
            return (int)l;
        }
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
        {
            return ClampDecimalToInt32(dec, ref clampedCount);
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
        {
            return ClampFiniteFloatingToInt32(dbl, ref clampedCount);
        }
        return 0;
    }

    private static long ToInt64(object? value, ref int coercedCount)
    {
        switch (value)
        {
            case null: return 0L;
            case long l: return l;
            case int i: return i;
            case short s: return s;
            case byte b: return b;
            case sbyte sb: return sb;
            case ushort us: return us;
            case uint ui: return ui;
            case ulong ul:
                if (ul > long.MaxValue)
                {
                    coercedCount++;
                    return long.MaxValue;
                }
                return (long)ul;
            case float f:
                return CoerceFiniteFloatingToInt64(f, ref coercedCount);
            case double d:
                return CoerceFiniteFloatingToInt64(d, ref coercedCount);
            case decimal m:
                return CoerceDecimalToInt64(m, ref coercedCount);
            case string s:
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                coercedCount++;
                return 0L;
            default:
                coercedCount++;
                return 0L;
        }
    }

    private static int ClampFiniteFloatingToInt32(double value, ref int clampedCount)
    {
        // NaN and ±Infinity have no defensible Int32 representation; treat as
        // "outside INT32 range" so the documented clamping warning fires
        // rather than letting the implementation-defined cast surface
        // garbage.
        if (!double.IsFinite(value))
        {
            clampedCount++;
            return 0;
        }
        if (value <= int.MinValue)
        {
            clampedCount++;
            return int.MinValue;
        }
        if (value >= (double)int.MaxValue + 1.0)
        {
            clampedCount++;
            return int.MaxValue;
        }
        return (int)value;
    }

    private static int ClampDecimalToInt32(decimal value, ref int clampedCount)
    {
        // (int)decimal cast is checked in C# and throws OverflowException for
        // out-of-range values, which would crash generation rather than
        // surface as the documented non-fatal warning. Clamp explicitly
        // before the cast.
        if (value < int.MinValue)
        {
            clampedCount++;
            return int.MinValue;
        }
        if (value > int.MaxValue)
        {
            clampedCount++;
            return int.MaxValue;
        }
        return (int)value;
    }

    private static long CoerceFiniteFloatingToInt64(double value, ref int coercedCount)
    {
        // Non-finite (NaN / ±Infinity) is non-integral by definition; emit
        // the documented coercion warning and return zero so the buffer is
        // deterministic.
        if (!double.IsFinite(value))
        {
            coercedCount++;
            return 0L;
        }
        // (double)long.MinValue is exactly -2^63 (representable). The cast
        // (long)(-2^63) is well-defined and equals long.MinValue, so values
        // strictly less than it must clamp.
        if (value < long.MinValue)
        {
            coercedCount++;
            return long.MinValue;
        }
        // (double)long.MaxValue rounds UP to 2^63 because (2^63 - 1) is not
        // representable as a finite double; values >= 2^63 are NOT valid
        // longs and the cast is implementation-defined, so clamp them.
        if (value >= (double)long.MaxValue)
        {
            coercedCount++;
            return long.MaxValue;
        }
        // In-range fractional values are still non-integral per the doc
        // contract — truncating 12.5 to 12 silently would lose the warning
        // that callers rely on to catch unexpected source types.
        var truncated = Math.Truncate(value);
        if (truncated != value)
        {
            coercedCount++;
        }
        return (long)truncated;
    }

    private static long CoerceDecimalToInt64(decimal value, ref int coercedCount)
    {
        // (long)decimal cast is checked in C# and throws OverflowException
        // outside [Int64.MinValue, Int64.MaxValue]. Clamp explicitly so a
        // non-integral source value produces the documented warning instead
        // of a 503-equivalent crash.
        if (value < long.MinValue)
        {
            coercedCount++;
            return long.MinValue;
        }
        if (value > long.MaxValue)
        {
            coercedCount++;
            return long.MaxValue;
        }
        var truncated = decimal.Truncate(value);
        if (truncated != value)
        {
            coercedCount++;
        }
        return (long)truncated;
    }
}
