// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
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
    /// <returns>A deterministic GLB byte sequence.</returns>
    public static byte[] BuildGlb(
        IReadOnlyList<SceneFeature> features,
        IReadOnlyList<SceneAttributeSchema> metadataAttributes,
        LayerExtrusionInfo? extrusion,
        string? generatorTag = null,
        IList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(metadataAttributes);

        if (features.Count == 0)
        {
            throw new ArgumentException("At least one feature is required to build a tile.", nameof(features));
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
        var positions = new List<float>(features.Count * 8);
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
            throw new InvalidOperationException("No vertices produced for tile.");
        }

        // 2. Build the per-feature property table buffers.
        var propertyTable = SceneMetadataTable.Build(features, metadataAttributes, warnings);

        // 3. Lay out the binary buffer: positions, feature ids, then each property column.
        var positionBytes = AsByteSpan(positions);
        var featureIdBytes = AsByteSpan(featureIds);

        var bufferViews = new List<BufferViewDescriptor>(2 + propertyTable.Columns.Count);
        var binaryWriter = new BinaryBufferBuilder();

        var positionView = binaryWriter.Append(positionBytes);
        bufferViews.Add(new BufferViewDescriptor(positionView, BufferViewTarget.ArrayBuffer));

        var featureIdView = binaryWriter.Append(featureIdBytes);
        bufferViews.Add(new BufferViewDescriptor(featureIdView, BufferViewTarget.ArrayBuffer));

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
            positions,
            bufferViews,
            positionViewIndex: 0,
            featureIdViewIndex: 1,
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
        LayerExtrusionInfo? extrusion,
        List<float> positions,
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
        List<float> positions,
        List<float> featureIds,
        float featureIndex)
    {
        var height = heightOverride ?? vertex.Height ?? 0.0;
        var (x, y, z) = EcefCoordinateTransform.ToEcef(vertex.Longitude, vertex.Latitude, height);
        positions.Add((float)x);
        positions.Add((float)y);
        positions.Add((float)z);
        featureIds.Add(featureIndex);
    }

    private static void AppendPolygonTriangles(
        SceneFeature feature,
        float featureIndex,
        LayerExtrusionInfo? extrusion,
        List<float> positions,
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
            ? ResolveExtrusionHeight(feature, extrusion!)
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
        var baseHeight = ResolveBaseHeight(feature, extrusion!);
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

    private static double ResolveExtrusionHeight(SceneFeature feature, LayerExtrusionInfo extrusion)
    {
        var raw = LookupNumericAttribute(feature, extrusion.HeightField) ?? extrusion.DefaultHeight ?? 0.0;
        return ConvertVerticalToMeters(raw, extrusion.Unit);
    }

    private static double ResolveBaseHeight(SceneFeature feature, LayerExtrusionInfo extrusion)
    {
        if (string.IsNullOrEmpty(extrusion.BaseHeightField))
        {
            return 0.0;
        }

        var raw = LookupNumericAttribute(feature, extrusion.BaseHeightField) ?? 0.0;
        return ConvertVerticalToMeters(raw, extrusion.Unit);
    }

    private static double? LookupNumericAttribute(SceneFeature feature, string fieldName)
    {
        if (!feature.Attributes.TryGetValue(fieldName, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => null
        };
    }

    private static double ConvertVerticalToMeters(double value, string? unit)
    {
        VerticalUnits.TryNormalize(unit, out var normalized);
        return normalized switch
        {
            VerticalUnits.Feet => value * 0.3048,
            VerticalUnits.UsSurveyFeet => value * (1200.0 / 3937.0),
            _ => value
        };
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
        List<float> positions,
        List<BufferViewDescriptor> bufferViews,
        int positionViewIndex,
        int featureIdViewIndex,
        int featureCount,
        SceneMetadataTable propertyTable,
        int[] propertyViewIndices,
        int totalBinaryLength,
        string? generatorTag)
    {
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
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("meshes");
            writer.WriteStartObject();
            writer.WriteStartArray("primitives");
            writer.WriteStartObject();

            writer.WriteStartObject("attributes");
            writer.WriteNumber("POSITION", 0);
            writer.WriteNumber("_FEATURE_ID_0", 1);
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

    private static (double, double, double, double, double, double) ComputePositionBounds(List<float> positions)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;

        for (var i = 0; i < positions.Count; i += 3)
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
        private readonly List<byte> _buffer = new();

        public BufferRange Append(byte[] payload, int alignment = 4)
        {
            // glTF 2.0 requires bufferView byteOffset to be aligned to the
            // accessor's component size (4 bytes for FLOAT/UINT32).
            // EXT_structural_metadata extends this for property-table values,
            // requiring alignment equal to the component byte length (8 for
            // INT64/UINT64). Pad with zeros to keep alignment deterministic.
            var mask = alignment - 1;
            var pad = (alignment - (_buffer.Count & mask)) & mask;
            for (var i = 0; i < pad; i++)
            {
                _buffer.Add(0);
            }
            var offset = _buffer.Count;
            _buffer.AddRange(payload);
            return new BufferRange(offset, payload.Length);
        }

        public byte[] GetPayloadWithPadding()
        {
            var pad = (4 - (_buffer.Count & 3)) & 3;
            for (var i = 0; i < pad; i++)
            {
                _buffer.Add(0);
            }
            return _buffer.ToArray();
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
        var stringBytes = new List<byte>(features.Count * 16);
        var offsets = new uint[features.Count + 1];
        offsets[0] = 0;
        for (var i = 0; i < features.Count; i++)
        {
            features[i].Attributes.TryGetValue(schema.FieldName, out var raw);
            var encoded = Encoding.UTF8.GetBytes(raw?.ToString() ?? string.Empty);
            stringBytes.AddRange(encoded);
            offsets[i + 1] = (uint)stringBytes.Count;
        }

        // glTF 2.0 requires bufferView.byteLength >= 1, so a column whose
        // values are all null/empty would otherwise emit an invalid zero-byte
        // values view. Pad with a single 0x00 byte; every offset stays at 0
        // and every string therefore decodes as empty (length = next - prev).
        var values = stringBytes.Count == 0
            ? new byte[] { 0 }
            : stringBytes.ToArray();

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
