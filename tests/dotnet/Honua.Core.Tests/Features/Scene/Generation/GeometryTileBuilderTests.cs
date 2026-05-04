// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Generation;

/// <summary>
/// Unit tests for the deterministic GLB tile builder.
/// </summary>
public sealed class GeometryTileBuilderTests
{
    [UnitTest]
    public void BuildGlb_ProducesGlbHeaderAndTwoChunks()
    {
        var glb = BuildSimpleSquare();

        // 12-byte GLB header: magic + version + length.
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4)).Should().Be(0x46546C67); // "glTF"
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4)).Should().Be(2u);
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4)).Should().Be((uint)glb.Length);

        // First chunk header at offset 12 must be JSON.
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16, 4)).Should().Be(0x4E4F534Au);
    }

    [UnitTest]
    public void BuildGlb_IsByteIdenticalAcrossRuns()
    {
        var glb1 = BuildSimpleSquare();
        var glb2 = BuildSimpleSquare();

        glb1.Should().Equal(glb2);
    }

    [UnitTest]
    public void BuildGlb_PreservesAttributesInStructuralMetadata()
    {
        var glb = BuildSimpleSquare();
        var json = ExtractJsonChunk(glb);

        using var doc = JsonDocument.Parse(json);
        var ext = doc.RootElement.GetProperty("extensions").GetProperty("EXT_structural_metadata");
        var classProps = ext.GetProperty("schema").GetProperty("classes")
            .GetProperty("honua_feature_class").GetProperty("properties");

        classProps.TryGetProperty("name", out _).Should().BeTrue();
        classProps.TryGetProperty("height", out _).Should().BeTrue();
    }

    [UnitTest]
    public void BuildGlb_EmitsFeatureIdAttributeAndPropertyTable()
    {
        var glb = BuildSimpleSquare();
        var json = ExtractJsonChunk(glb);

        using var doc = JsonDocument.Parse(json);
        var primitive = doc.RootElement.GetProperty("meshes")[0]
            .GetProperty("primitives")[0];

        primitive.GetProperty("attributes").TryGetProperty("_FEATURE_ID_0", out _).Should().BeTrue();
        var ext = primitive.GetProperty("extensions").GetProperty("EXT_mesh_features");
        var featureIds = ext.GetProperty("featureIds")[0];
        featureIds.GetProperty("featureCount").GetInt32().Should().Be(2);
        featureIds.GetProperty("propertyTable").GetInt32().Should().Be(0);
    }

    [UnitTest]
    public void BuildGlb_AppliesExtrusionToProducePositiveZ()
    {
        var feature = new SceneFeature
        {
            Id = 1,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.Polygon,
                Vertices = SquareRing()
            },
            Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["height"] = 25.0
            }
        };

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "height", FieldName = "height", SchemaType = "SCALAR", SchemaComponentType = "FLOAT32" }
        };

        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height",
            Unit = VerticalUnits.Meters
        };

        var glb = GeometryTileBuilder.BuildGlb([feature], schemas, extrusion);
        glb.Length.Should().BeGreaterThan(0);

        // Sanity: extruded prism should produce more vertices than a flat
        // polygon (top fan + bottom fan + 6 verts per wall).
        var glbFlat = GeometryTileBuilder.BuildGlb(
            [feature with { Attributes = new Dictionary<string, object?>(StringComparer.Ordinal) }],
            schemas,
            extrusion: null);
        glb.Length.Should().BeGreaterThan(glbFlat.Length);
    }

    [UnitTest]
    public void BuildGlb_RejectsHeterogeneousGeometryTypes()
    {
        var pointFeature = new SceneFeature
        {
            Id = 1,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.Point,
                Vertices = new[] { new SceneVertex(0, 0, null) }
            }
        };
        var lineFeature = new SceneFeature
        {
            Id = 2,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.LineString,
                Vertices = new[] { new SceneVertex(0, 0, null), new SceneVertex(1, 1, null) }
            }
        };

        var act = () => GeometryTileBuilder.BuildGlb(
            [pointFeature, lineFeature],
            metadataAttributes: Array.Empty<SceneAttributeSchema>(),
            extrusion: null);

        act.Should().Throw<ArgumentException>().WithMessage("*share the same geometry kind*");
    }

    [UnitTest]
    public void BuildGlb_AllNullStringMetadata_EmitsValidNonZeroValuesBufferView()
    {
        // glTF 2.0 requires bufferView.byteLength >= 1, so a STRING column
        // whose values are all null (or all empty) must still emit a values
        // buffer view of at least one byte. The string offsets stay at 0,
        // so every feature still resolves to an empty string of length 0.
        var features = new[]
        {
            new SceneFeature
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = SquareRing()
                },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = null
                }
            },
            new SceneFeature
            {
                Id = 2,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = SquareRing(offsetLon: 0.001)
                },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = string.Empty
                }
            }
        };

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "name", FieldName = "name", SchemaType = "STRING", SchemaComponentType = string.Empty }
        };

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null);
        var json = ExtractJsonChunk(glb);

        using var doc = JsonDocument.Parse(json);
        var nameProperty = doc.RootElement.GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("propertyTables")[0]
            .GetProperty("properties").GetProperty("name");
        var valuesView = nameProperty.GetProperty("values").GetInt32();
        var stringOffsetsView = nameProperty.GetProperty("stringOffsets").GetInt32();

        var bufferViews = doc.RootElement.GetProperty("bufferViews");
        var valuesByteLength = bufferViews[valuesView].GetProperty("byteLength").GetInt32();
        var offsetsByteLength = bufferViews[stringOffsetsView].GetProperty("byteLength").GetInt32();

        valuesByteLength.Should().BeGreaterThanOrEqualTo(1,
            "glTF 2.0 forbids zero-length bufferViews; an all-null STRING column must pad its values view to at least one byte.");
        offsetsByteLength.Should().Be((features.Length + 1) * 4,
            "string offsets remain (count+1)*4 bytes regardless of value content; every offset is 0 for empty strings.");

        // Verify every offset is 0 — readers slicing the values buffer with
        // those offsets get zero-length spans (correct empty strings).
        var binPayload = ExtractBinChunk(glb);
        var offsetsByteOffset = bufferViews[stringOffsetsView].GetProperty("byteOffset").GetInt32();
        for (var i = 0; i <= features.Length; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(
                binPayload.AsSpan(offsetsByteOffset + i * 4, 4));
            offset.Should().Be(0u,
                "all-null/all-empty string offsets stay at 0 so every value decodes as empty string.");
        }
    }

    [UnitTest]
    public void BuildGlb_PreservesInt64AttributesBeyondInt32Range()
    {
        var beyondMax = (long)int.MaxValue + 100L;
        var beyondMin = (long)int.MinValue - 100L;

        var features = new[]
        {
            new SceneFeature
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = SquareRing()
                },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["big_id"] = beyondMax
                }
            },
            new SceneFeature
            {
                Id = 2,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = SquareRing(offsetLon: 0.001)
                },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["big_id"] = beyondMin
                }
            }
        };

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "big_id", FieldName = "big_id", SchemaType = "SCALAR", SchemaComponentType = "INT64" }
        };

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null);
        var json = ExtractJsonChunk(glb);

        using var doc = JsonDocument.Parse(json);
        var classProps = doc.RootElement.GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("schema").GetProperty("classes")
            .GetProperty("honua_feature_class").GetProperty("properties");
        classProps.GetProperty("big_id").GetProperty("componentType").GetString().Should().Be("INT64");

        var bigIdView = doc.RootElement.GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("propertyTables")[0]
            .GetProperty("properties").GetProperty("big_id")
            .GetProperty("values").GetInt32();

        var bufferView = doc.RootElement.GetProperty("bufferViews")[bigIdView];
        var byteOffset = bufferView.GetProperty("byteOffset").GetInt32();
        var byteLength = bufferView.GetProperty("byteLength").GetInt32();
        (byteOffset % 8).Should().Be(0, "INT64 buffer views must be 8-byte aligned per EXT_structural_metadata");
        byteLength.Should().Be(features.Length * 8);

        var binPayload = ExtractBinChunk(glb);
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset, 8)).Should().Be(beyondMax);
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset + 8, 8)).Should().Be(beyondMin);
    }

    [UnitTest]
    public void BuildGlb_Int64Column_FractionalDoubleEmitsCoercionWarning()
    {
        // The doc contract says fractional values are non-integral and must
        // emit the coercion warning. The previous implementation only
        // counted out-of-range values, so a fractional in-range double like
        // 12.5 was silently truncated.
        var features = NumericFeaturePair(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["big_id"] = 12.5 },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["big_id"] = 99.75 });

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "big_id", FieldName = "big_id", SchemaType = "SCALAR", SchemaComponentType = "INT64" }
        };
        var warnings = new List<string>();

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);

        warnings.Should().ContainSingle(w => w.Contains("big_id", StringComparison.Ordinal)
            && w.Contains("coerced", StringComparison.Ordinal)
            && w.Contains("INT64", StringComparison.Ordinal),
            "fractional doubles on an INT64 metadata column must surface the documented non-integral coercion warning.");

        var (byteOffset, _) = GetInt64BufferRange(glb, "big_id");
        var binPayload = ExtractBinChunk(glb);
        // Math.Truncate(12.5) = 12; Math.Truncate(99.75) = 99
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset, 8)).Should().Be(12L);
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset + 8, 8)).Should().Be(99L);
    }

    [UnitTest]
    public void BuildGlb_Int64Column_DecimalMaxValueClampsWithoutThrowing()
    {
        // (long)decimal.MaxValue is a CHECKED cast in C# and throws
        // OverflowException; the previous implementation would crash the
        // generation rather than emitting the documented non-fatal warning.
        var features = NumericFeaturePair(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["big_id"] = decimal.MaxValue },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["big_id"] = decimal.MinValue });

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "big_id", FieldName = "big_id", SchemaType = "SCALAR", SchemaComponentType = "INT64" }
        };
        var warnings = new List<string>();

        Action act = () => GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);
        act.Should().NotThrow<OverflowException>();

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);
        var (byteOffset, _) = GetInt64BufferRange(glb, "big_id");
        var binPayload = ExtractBinChunk(glb);
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset, 8)).Should().Be(long.MaxValue);
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset + 8, 8)).Should().Be(long.MinValue);

        warnings.Should().Contain(w => w.Contains("big_id", StringComparison.Ordinal)
            && w.Contains("coerced", StringComparison.Ordinal),
            "out-of-range decimals must surface the non-integral coercion warning instead of throwing.");
    }

    [UnitTest]
    public void BuildGlb_Int64Column_NonFiniteFloatingClampsToZeroWithWarning()
    {
        var features = NumericFeaturePair(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["big_id"] = double.PositiveInfinity },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["big_id"] = double.NaN });

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "big_id", FieldName = "big_id", SchemaType = "SCALAR", SchemaComponentType = "INT64" }
        };
        var warnings = new List<string>();

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);
        var (byteOffset, _) = GetInt64BufferRange(glb, "big_id");
        var binPayload = ExtractBinChunk(glb);
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset, 8)).Should().Be(0L,
            "non-finite values have no defensible Int64 representation; coerce to zero deterministically.");
        BinaryPrimitives.ReadInt64LittleEndian(binPayload.AsSpan(byteOffset + 8, 8)).Should().Be(0L);

        warnings.Should().Contain(w => w.Contains("big_id", StringComparison.Ordinal)
            && w.Contains("coerced", StringComparison.Ordinal));
    }

    [UnitTest]
    public void BuildGlb_Int32Column_OutOfRangeNumericStringClampsWithWarning()
    {
        // Postgres scene feature source projects JSONB attributes through
        // `attributes ->> @field` which always returns TEXT. A native
        // bigint value above Int32.MaxValue therefore reaches the writer
        // as the string "2147483648"; without explicit string-numeric
        // fallback it silently becomes 0 with no clamping warning.
        var features = NumericFeaturePair(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["height"] = "2147483648" },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["height"] = "-2147483649" });

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "height", FieldName = "height", SchemaType = "SCALAR", SchemaComponentType = "INT32" }
        };
        var warnings = new List<string>();

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);

        var (byteOffset, _) = GetInt32BufferRange(glb, "height");
        var binPayload = ExtractBinChunk(glb);
        BinaryPrimitives.ReadInt32LittleEndian(binPayload.AsSpan(byteOffset, 4)).Should().Be(int.MaxValue,
            "out-of-range positive integer strings must clamp to Int32.MaxValue, not silently flatten to 0.");
        BinaryPrimitives.ReadInt32LittleEndian(binPayload.AsSpan(byteOffset + 4, 4)).Should().Be(int.MinValue,
            "out-of-range negative integer strings must clamp to Int32.MinValue.");

        warnings.Should().Contain(w => w.Contains("height", StringComparison.Ordinal)
            && w.Contains("clamped", StringComparison.Ordinal),
            "out-of-range numeric strings on an INT32 column must surface the documented clamping warning.");
    }

    [UnitTest]
    public void BuildGlb_EmitsDoubleSidedMaterialReferencedByPrimitive()
    {
        // glTF 2.0 §3.6.4: the default material has doubleSided=false, so
        // back-face culling drops triangles whose vertices wind clockwise
        // when viewed from the camera. PostGIS polygon outer rings are
        // typically CCW but not guaranteed; emit a single double-sided
        // default material so any source winding renders both sides in
        // every standards-compliant viewer.
        var glb = BuildSimpleSquare();
        var json = ExtractJsonChunk(glb);

        using var doc = JsonDocument.Parse(json);

        var materials = doc.RootElement.GetProperty("materials");
        materials.GetArrayLength().Should().Be(1);
        materials[0].GetProperty("doubleSided").GetBoolean().Should().BeTrue();

        var primitive = doc.RootElement.GetProperty("meshes")[0]
            .GetProperty("primitives")[0];
        primitive.GetProperty("material").GetInt32().Should().Be(0,
            "every primitive must reference the double-sided material so back-face culling cannot drop CW rings.");
    }

    [UnitTest]
    public void BuildGlb_Int32Column_DecimalMaxValueClampsWithoutThrowing()
    {
        // Companion fix: (int)decimal cast also throws on out-of-range
        // values. Ensure the Integer column's clamping helper survives
        // decimal.MaxValue without crashing the generation.
        var features = NumericFeaturePair(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["height"] = decimal.MaxValue },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["height"] = decimal.MinValue });

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "height", FieldName = "height", SchemaType = "SCALAR", SchemaComponentType = "INT32" }
        };
        var warnings = new List<string>();

        Action act = () => GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);
        act.Should().NotThrow<OverflowException>();

        var glb = GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null, warnings: warnings);
        var (byteOffset, _) = GetInt32BufferRange(glb, "height");
        var binPayload = ExtractBinChunk(glb);
        BinaryPrimitives.ReadInt32LittleEndian(binPayload.AsSpan(byteOffset, 4)).Should().Be(int.MaxValue);
        BinaryPrimitives.ReadInt32LittleEndian(binPayload.AsSpan(byteOffset + 4, 4)).Should().Be(int.MinValue);

        warnings.Should().Contain(w => w.Contains("height", StringComparison.Ordinal)
            && w.Contains("clamped", StringComparison.Ordinal));
    }

    private static SceneFeature[] NumericFeaturePair(
        Dictionary<string, object?> firstAttrs,
        Dictionary<string, object?> secondAttrs)
    {
        return new[]
        {
            new SceneFeature
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry { Kind = SceneGeometryKind.Polygon, Vertices = SquareRing() },
                Attributes = firstAttrs
            },
            new SceneFeature
            {
                Id = 2,
                Geometry = new SceneFeatureGeometry { Kind = SceneGeometryKind.Polygon, Vertices = SquareRing(offsetLon: 0.001) },
                Attributes = secondAttrs
            }
        };
    }

    private static (int ByteOffset, int ByteLength) GetInt64BufferRange(byte[] glb, string propertyId)
        => GetMetadataBufferRange(glb, propertyId, expectedAlignmentMod: 8);

    private static (int ByteOffset, int ByteLength) GetInt32BufferRange(byte[] glb, string propertyId)
        => GetMetadataBufferRange(glb, propertyId, expectedAlignmentMod: null);

    private static (int ByteOffset, int ByteLength) GetMetadataBufferRange(byte[] glb, string propertyId, int? expectedAlignmentMod)
    {
        var json = ExtractJsonChunk(glb);
        using var doc = JsonDocument.Parse(json);
        var viewIndex = doc.RootElement.GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("propertyTables")[0]
            .GetProperty("properties").GetProperty(propertyId)
            .GetProperty("values").GetInt32();
        var bufferView = doc.RootElement.GetProperty("bufferViews")[viewIndex];
        var byteOffset = bufferView.GetProperty("byteOffset").GetInt32();
        var byteLength = bufferView.GetProperty("byteLength").GetInt32();
        if (expectedAlignmentMod is { } mod)
        {
            (byteOffset % mod).Should().Be(0,
                $"the {propertyId} bufferView must satisfy {mod}-byte alignment for the declared component type.");
        }
        return (byteOffset, byteLength);
    }

    private static byte[] BuildSimpleSquare()
    {
        var features = new[]
        {
            new SceneFeature
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = SquareRing()
                },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "alpha",
                    ["height"] = 10
                }
            },
            new SceneFeature
            {
                Id = 2,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = SquareRing(offsetLon: 0.001)
                },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "beta",
                    ["height"] = 20
                }
            }
        };

        var schemas = new[]
        {
            new SceneAttributeSchema { PropertyId = "name", FieldName = "name", SchemaType = "STRING", SchemaComponentType = "" },
            new SceneAttributeSchema { PropertyId = "height", FieldName = "height", SchemaType = "SCALAR", SchemaComponentType = "INT32" }
        };

        return GeometryTileBuilder.BuildGlb(features, schemas, extrusion: null);
    }

    private static SceneVertex[] SquareRing(double offsetLon = 0)
    {
        return new[]
        {
            new SceneVertex(-122.5 + offsetLon, 37.7, 0),
            new SceneVertex(-122.4 + offsetLon, 37.7, 0),
            new SceneVertex(-122.4 + offsetLon, 37.8, 0),
            new SceneVertex(-122.5 + offsetLon, 37.8, 0),
            new SceneVertex(-122.5 + offsetLon, 37.7, 0)
        };
    }

    private static string ExtractJsonChunk(byte[] glb)
    {
        // GLB header is 12 bytes; chunk header is 8 bytes (length + type).
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        var jsonBytes = glb.AsSpan(20, jsonLength);
        return Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0', ' ');
    }

    private static byte[] ExtractBinChunk(byte[] glb)
    {
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        var binChunkOffset = 20 + jsonLength;
        var binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binChunkOffset, 4));
        return glb.AsSpan(binChunkOffset + 8, binLength).ToArray();
    }
}
