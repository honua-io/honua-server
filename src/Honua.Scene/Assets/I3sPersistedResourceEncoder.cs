// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Scene.Assets;

/// <summary>Encodes resource bytes and their corresponding declared layouts together.</summary>
internal static class I3sPersistedResourceEncoder
{
    public static I3sGeometryDefinition GeometryDefinition => new()
    {
        Topology = "triangle",
        GeometryBuffers = [new()
        {
            Offset = 8,
            Position = new() { Type = "Float32", Component = 3 },
            Normal = new() { Type = "Float32", Component = 3 },
            Uv0 = new() { Type = "Float32", Component = 2 },
            Uv1 = new() { Type = "Float32", Component = 2 },
            Color = new() { Type = "UInt8", Component = 4 },
            FeatureId = new() { Type = "UInt64", Component = 1, Binding = "per-feature" },
            FaceRange = new() { Type = "UInt32", Component = 2, Binding = "per-feature" },
        }],
    };

    public static I3sDefaultGeometrySchema GeometrySchema => new()
    {
        GeometryType = "triangles",
        Header = [new() { Property = "vertexCount", Type = "UInt32" }, new() { Property = "featureCount", Type = "UInt32" }],
        Topology = "PerAttributeArray",
        Ordering = ["position", "normal", "uv0", "uv1", "color"],
        VertexAttributes = new Dictionary<string, I3sAttributeValues>
        {
            ["position"] = new() { ValueType = "Float32", ValuesPerElement = 3 },
            ["normal"] = new() { ValueType = "Float32", ValuesPerElement = 3 },
            ["uv0"] = new() { ValueType = "Float32", ValuesPerElement = 2 },
            ["uv1"] = new() { ValueType = "Float32", ValuesPerElement = 2 },
            ["color"] = new() { ValueType = "UInt8", ValuesPerElement = 4 },
        },
        FeatureAttributeOrder = ["id", "faceRange"],
        FeatureAttributes = new Dictionary<string, I3sAttributeValues>
        {
            ["id"] = new() { ValueType = "UInt64", ValuesPerElement = 1 },
            ["faceRange"] = new() { ValueType = "UInt32", ValuesPerElement = 2 },
        },
    };

    public static IReadOnlyList<I3sAttributeStorageInfo> BuildAttributes(IReadOnlyList<SceneCachedFeature> features)
    {
        var result = new List<I3sAttributeStorageInfo> { Descriptor("f_0", "OBJECTID", "Oid32") };
        var names = features.SelectMany(feature => feature.Attributes.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var values = features.Select(feature => feature.Attributes.GetValueOrDefault(name)).Where(value => value is not null).ToArray();
            var type = values.All(value => value is string) ? "String"
                : values.All(value => value is bool) ? "UInt8"
                : values.All(value => value is long or int) ? "Int64"
                : values.All(value => value is long or int or double or float) ? "Float64"
                : throw new InvalidDataException("Incompatible scene attribute types.");
            result.Add(Descriptor("f_" + result.Count.ToString(CultureInfo.InvariantCulture), name, type));
        }

        return result;
    }

    public static IReadOnlyList<I3sField> BuildFields(IReadOnlyList<I3sAttributeStorageInfo> attributes) => attributes.Select(field => new I3sField
    {
        Name = field.Name!,
        Alias = field.Name!,
        Type = field.AttributeValues!.ValueType switch
        {
            "Oid32" => "esriFieldTypeOID",
            "String" => "esriFieldTypeString",
            "UInt8" => "esriFieldTypeSmallInteger",
            "Int64" => "esriFieldTypeBigInteger",
            _ => "esriFieldTypeDouble",
        },
    }).ToArray();

    public static I3sNodeResources Encode(SceneDecodedMesh mesh, I3sOrientedBoundingBox bounds,
        IReadOnlyList<I3sAttributeStorageInfo> attributes, CancellationToken cancellationToken)
    {
        var groups = mesh.Triangles.GroupBy(triangle => triangle.Feature).OrderBy(group => group.Key).ToArray();
        var triangles = groups.SelectMany(group => group).ToArray();
        var vertices = triangles.Length * 3;
        var features = groups.Select(group => mesh.Features[group.Key]).ToArray();
        var bytes = new byte[checked(8 + vertices * 44 + features.Length * 16)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)vertices);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)features.Length);
        var positionOffset = 8;
        var normalOffset = positionOffset + vertices * 12;
        var uvOffset = normalOffset + vertices * 12;
        var uv1Offset = uvOffset + vertices * 8;
        var colorOffset = uv1Offset + vertices * 8;
        var vertex = 0;
        foreach (var triangle in triangles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var point in new[] { triangle.A, triangle.B, triangle.C })
            {
                var geographic = EcefCoordinateTransform.FromEcef(point.Position.X, point.Position.Y, point.Position.Z);
                WriteFloat(bytes, positionOffset + vertex * 12, geographic.Longitude - bounds.Center[0]);
                WriteFloat(bytes, positionOffset + vertex * 12 + 4, geographic.Latitude - bounds.Center[1]);
                WriteFloat(bytes, positionOffset + vertex * 12 + 8, geographic.Height - bounds.Center[2]);
                var normal = point.Normal ?? triangle.Normal;
                WriteFloat(bytes, normalOffset + vertex * 12, normal.X);
                WriteFloat(bytes, normalOffset + vertex * 12 + 4, normal.Y);
                WriteFloat(bytes, normalOffset + vertex * 12 + 8, normal.Z);
                WriteFloat(bytes, uvOffset + vertex * 8, point.U);
                WriteFloat(bytes, uvOffset + vertex * 8 + 4, point.V);
                WriteFloat(bytes, uv1Offset + vertex * 8, point.U1);
                WriteFloat(bytes, uv1Offset + vertex * 8 + 4, point.V1);
                bytes[colorOffset + vertex * 4] = Color(point.R);
                bytes[colorOffset + vertex * 4 + 1] = Color(point.G);
                bytes[colorOffset + vertex * 4 + 2] = Color(point.B);
                bytes[colorOffset + vertex * 4 + 3] = Channel(point.A);
                vertex++;
            }
        }

        var featureOffset = 8 + vertices * 44;
        var faceOffset = featureOffset + features.Length * 8;
        var face = 0;
        for (var index = 0; index < groups.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(featureOffset + index * 8), (ulong)features[index].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(faceOffset + index * 8), (uint)face);
            face += groups[index].Count();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(faceOffset + index * 8 + 4), (uint)(face - 1));
        }

        return new(new(bytes, vertices, features.Length),
            attributes.ToDictionary(field => field.Key!, field => EncodeAttribute(features, field), StringComparer.Ordinal),
            mesh.Textures.ToDictionary(pair => pair.Key, pair => pair.Value.Resource, StringComparer.Ordinal));
    }

    public static I3sOrientedBoundingBox Bounds(IEnumerable<SceneCartesian> source)
    {
        var points = source.ToArray();
        if (points.Length == 0)
        {
            throw new InvalidDataException("A scene node has no geometric extent.");
        }

        var min = new SceneCartesian(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var max = new SceneCartesian(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
        var centre = (min + max) / 2;
        var geographic = EcefCoordinateTransform.FromEcef(centre.X, centre.Y, centre.Z);
        return new()
        {
            Center = [geographic.Longitude, geographic.Latitude, geographic.Height],
            HalfSize = [(max.X - min.X) / 2, (max.Y - min.Y) / 2, (max.Z - min.Z) / 2],
            Quaternion = [0, 0, 0, 1]
        };
    }

    public static I3sMaterialDefinition Material(SceneDecodedMesh mesh, List<I3sTextureSetDefinition> textureSets)
    {
        var source = mesh.Material;
        var pbr = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("pbrMetallicRoughness", out var encodedPbr) ? encodedPbr : default;
        var color = Vector(pbr, "baseColorFactor", [1, 1, 1, 1]);
        var alpha = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("alphaMode", out var encodedAlpha) ? encodedAlpha.GetString() : "OPAQUE";
        if (alpha is not ("OPAQUE" or "MASK" or "BLEND"))
        {
            throw new InvalidDataException("Invalid material alpha mode.");
        }

        var doubleSided = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("doubleSided", out var sided) && sided.GetBoolean();

        return new()
        {
            PbrMetallicRoughness = new()
            {
                BaseColorFactor = [Srgb(color[0]), Srgb(color[1]), Srgb(color[2]), color[3]],
                MetallicFactor = Scalar(pbr, "metallicFactor", 1),
                RoughnessFactor = Scalar(pbr, "roughnessFactor", 1),
                BaseColorTexture = Texture("0"),
                MetallicRoughnessTexture = Texture("1"),
            },
            NormalTexture = Texture("2"),
            OcclusionTexture = Texture("3"),
            EmissiveTexture = Texture("4"),
            EmissiveFactor = Vector(source, "emissiveFactor", [0, 0, 0]).Select(Srgb).ToArray(),
            AlphaMode = alpha.ToLowerInvariant(),
            AlphaCutoff = alpha == "MASK" ? Scalar(source, "alphaCutoff", 0.5) : null,
            DoubleSided = doubleSided,
            CullFace = doubleSided ? "none" : "back",
        };

        I3sMaterialTexture? Texture(string key)
        {
            if (!mesh.Textures.TryGetValue(key, out var texture))
            {
                return null;
            }

            var definition = textureSets.Count;
            textureSets.Add(new() { Formats = [new() { Name = key, Format = texture.Resource.Format }] });
            return new() { TextureSetDefinitionId = definition, TexCoord = texture.TexCoord, Factor = texture.Factor };
        }
    }

    public static IReadOnlyDictionary<string, I3sAttributeStatisticsDocument> Statistics(IReadOnlyList<SceneCachedFeature> features,
        IReadOnlyList<I3sAttributeStorageInfo> attributes) => attributes.ToDictionary(field => field.Key!, field =>
        {
            var values = features.Select(feature => Value(feature, field.Name!)).Where(value => value is not null).ToArray();
            // The public statistics model uses double extrema. Omit them when
            // a stored integral value would be rounded; counts and Int64 buffers
            // remain exact instead of advertising a different numeric range.
            var numeric = field.AttributeValues!.ValueType != "String" && values.All(IsExactlyRepresentable);
            return new I3sAttributeStatisticsDocument
            {
                Stats = new()
                {
                    TotalValuesCount = values.Length,
                    Count = values.Distinct().LongCount(),
                    Min = numeric && values.Length > 0 ? values.Min(value => Convert.ToDouble(value, CultureInfo.InvariantCulture)) : null,
                    Max = numeric && values.Length > 0 ? values.Max(value => Convert.ToDouble(value, CultureInfo.InvariantCulture)) : null,
                }
            };
        }, StringComparer.Ordinal);

    private static bool IsExactlyRepresentable(object? value)
    {
        if (value is not long integer) { return true; }
        var number = (double)integer;
        return number < 9223372036854775808d && (long)number == integer;
    }

    private static I3sAttributeStorageInfo Descriptor(string key, string name, string type) => new()
    {
        Key = key,
        Name = name,
        Ordering = type == "String" ? ["attributeByteCounts", "attributeValues"] : ["attributeValues"],
        Header = type == "String" ? [new() { Property = "count", ValueType = "UInt32" }, new() { Property = "attributeValuesByteCount", ValueType = "UInt32" }]
            : [new() { Property = "count", ValueType = "UInt32" }],
        AttributeByteCounts = type == "String" ? new() { ValueType = "UInt32", ValuesPerElement = 1 } : null,
        AttributeValues = new() { ValueType = type, ValuesPerElement = 1, Encoding = type == "String" ? "UTF-8" : null },
    };

    private static byte[] EncodeAttribute(SceneCachedFeature[] features, I3sAttributeStorageInfo field)
    {
        var values = features.Select(feature => Value(feature, field.Name!)).ToArray();
        var type = field.AttributeValues!.ValueType;
        if (type == "String")
        {
            var strings = values.Select(value => value is null ? [] : Encoding.UTF8.GetBytes((string)value + '\0')).ToArray();
            var result = new byte[checked(8 + features.Length * 4 + strings.Sum(value => value.Length))];
            BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)features.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)strings.Sum(value => value.Length));
            var offset = 8 + features.Length * 4;
            for (var index = 0; index < strings.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8 + index * 4), (uint)strings[index].Length);
                strings[index].CopyTo(result, offset);
                offset += strings[index].Length;
            }

            return result;
        }

        var width = type is "Float64" or "Int64" ? 8 : type == "UInt8" ? 1 : 4;
        var header = width == 8 ? 8 : 4;
        var buffer = new byte[checked(header + features.Length * width)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)features.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var destination = buffer.AsSpan(header + index * width);
            var value = values[index];
            switch (type)
            {
                case "Oid32": BinaryPrimitives.WriteInt32LittleEndian(destination, checked((int)(long)value!)); break;
                case "Int64":
                    if (value is null) { throw new InvalidDataException("Nullable integer attributes require an explicit source null encoding."); }
                    BinaryPrimitives.WriteInt64LittleEndian(destination, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
                case "UInt8":
                    if (value is null) { throw new InvalidDataException("Nullable Boolean attributes require an explicit source null encoding."); }
                    destination[0] = (bool)value ? (byte)1 : (byte)0; break;
                case "Float64":
                    var number = value is null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (value is not null && (!double.IsFinite(number) || value is long integer && (long)number != integer))
                    { throw new InvalidDataException("A scene scalar cannot be narrowed without losing its value."); }
                    BinaryPrimitives.WriteDoubleLittleEndian(destination, number); break;
            }
        }

        return buffer;
    }

    private static object? Value(SceneCachedFeature feature, string name) => name == "OBJECTID" ? feature.Id : feature.Attributes.GetValueOrDefault(name);
    private static void WriteFloat(byte[] buffer, int offset, double value)
    {
        var narrowed = (float)value;
        if (!float.IsFinite(narrowed)) { throw new InvalidDataException("Geometry component exceeds Float32 range."); }
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), narrowed);
    }
    private static byte Color(double value) => Channel(Srgb(value));
    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    // The transfer function preserves black and white exactly; evaluating its
    // nonlinear branch at white otherwise introduces a rounding error.
    private static double Srgb(double value) => value is 0 or 1 ? value
        : value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
    private static double Scalar(JsonElement source, string property, double fallback)
    {
        var value = source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out var scalar) ? scalar.GetDouble() : fallback;
        return double.IsFinite(value) && value >= 0 && value <= 1 ? value : throw new InvalidDataException("Invalid material factor.");
    }
    private static double[] Vector(JsonElement source, string property, double[] fallback)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out var vector)) { return fallback; }
        if (vector.GetArrayLength() != fallback.Length) { throw new InvalidDataException("Invalid material color."); }
        var result = vector.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        return result.All(value => double.IsFinite(value) && value >= 0 && value <= 1) ? result : throw new InvalidDataException("Invalid material color.");
    }
}
