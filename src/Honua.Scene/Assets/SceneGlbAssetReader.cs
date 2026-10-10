// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Honua.Scene.Assets;

/// <summary>Reads persisted uncompressed GLB/B3DM mesh data without accessing a source database.</summary>
internal sealed class SceneGlbAssetReader
{
    private const int MaxVertices = 250_000;
    private readonly JsonElement _document;
    private readonly byte[] _binary;
    private readonly IReadOnlyList<SceneCachedFeature> _features;
    private readonly Func<string, byte[]> _readRelativeAsset;
    private readonly List<SceneMeshTriangle> _triangles = [];
    private readonly HashSet<int> _activeNodes = [];
    private readonly CancellationToken _cancellationToken;

    private SceneGlbAssetReader(JsonElement document, byte[] binary, IReadOnlyList<SceneCachedFeature> features, Func<string, byte[]> readRelativeAsset, CancellationToken cancellationToken)
    {
        _document = document;
        _binary = binary;
        _features = features;
        _readRelativeAsset = readRelativeAsset;
        _cancellationToken = cancellationToken;
    }

    public static IReadOnlyList<SceneDecodedMesh> Read(byte[] container, double[] tileTransform, string? upAxis, Func<string, byte[]> readRelativeAsset, CancellationToken cancellationToken)
    {
        var glb = container.AsMemory();
        var batchCount = 0;
        JsonElement batch = default;
        var rtc = new SceneCartesian();
        if (container.Length >= 28 && container.AsSpan(0, 4).SequenceEqual("b3dm"u8))
        {
            if (ReadUInt32(container, 4) != 1 || ReadUInt32(container, 8) != container.Length)
            {
                throw new InvalidDataException("Invalid B3DM header.");
            }

            var featureJsonLength = checked((int)ReadUInt32(container, 12));
            var featureBinaryLength = checked((int)ReadUInt32(container, 16));
            var batchJsonLength = checked((int)ReadUInt32(container, 20));
            var batchBinaryLength = checked((int)ReadUInt32(container, 24));
            var offset = checked(28 + featureJsonLength + featureBinaryLength + batchJsonLength + batchBinaryLength);
            if (featureJsonLength <= 0 || offset > container.Length - 20)
            {
                throw new InvalidDataException("Invalid B3DM table ranges.");
            }

            using var featureDocument = JsonDocument.Parse(container.AsMemory(28, featureJsonLength));
            var feature = featureDocument.RootElement;
            batchCount = feature.GetProperty("BATCH_LENGTH").GetInt32();
            if (batchCount < 0 || batchCount > MaxVertices)
            {
                throw new InvalidDataException("Invalid B3DM batch count.");
            }

            if (feature.TryGetProperty("RTC_CENTER", out var centre))
            {
                if (centre.ValueKind == JsonValueKind.Array && centre.GetArrayLength() == 3)
                {
                    rtc = new(centre[0].GetDouble(), centre[1].GetDouble(), centre[2].GetDouble());
                }
                else if (centre.ValueKind == JsonValueKind.Object && centre.TryGetProperty("byteOffset", out var rtcOffset))
                {
                    var start = checked(28 + featureJsonLength + rtcOffset.GetInt32());
                    if (start < 28 + featureJsonLength || start > 28 + featureJsonLength + featureBinaryLength - 12)
                    {
                        throw new InvalidDataException("Invalid RTC centre.");
                    }

                    rtc = new(ReadFloat(container, start), ReadFloat(container, start + 4), ReadFloat(container, start + 8));
                }
                else
                {
                    throw new InvalidDataException("Invalid RTC centre.");
                }
            }

            if (!rtc.IsFinite)
            {
                throw new InvalidDataException("Non-finite RTC centre.");
            }

            if (batchJsonLength > 0)
            {
                using var batchDocument = JsonDocument.Parse(container.AsMemory(28 + featureJsonLength + featureBinaryLength, batchJsonLength));
                batch = batchDocument.RootElement.Clone();
            }

            // Binary batch properties are decoded alongside their JSON declarations below.
            var batchBinary = container.AsSpan(28 + featureJsonLength + featureBinaryLength + batchJsonLength, batchBinaryLength).ToArray();
            var rows = ReadBatchTable(batch, batchBinary, batchCount);
            glb = container.AsMemory(offset);
            return ReadGlb(glb, tileTransform, upAxis, rtc, rows, readRelativeAsset, cancellationToken);
        }

        return ReadGlb(glb, tileTransform, upAxis, rtc, null, readRelativeAsset, cancellationToken);
    }

    private static SceneDecodedMesh[] ReadGlb(ReadOnlyMemory<byte> bytes, double[] tileTransform, string? upAxis, SceneCartesian rtc,
        IReadOnlyList<SceneCachedFeature>? batchRows, Func<string, byte[]> readRelativeAsset, CancellationToken cancellationToken)
    {
        var span = bytes.Span;
        if (span.Length < 20 || !span[..4].SequenceEqual("glTF"u8) || ReadUInt32(span, 4) != 2 || ReadUInt32(span, 8) != span.Length)
        {
            throw new InvalidDataException("Invalid GLB header.");
        }

        var jsonLength = checked((int)ReadUInt32(span, 12));
        if (jsonLength <= 0 || jsonLength % 4 != 0 || ReadUInt32(span, 16) != 0x4e4f534a || jsonLength > span.Length - 28)
        {
            throw new InvalidDataException("Invalid GLB JSON chunk.");
        }

        using var document = JsonDocument.Parse(bytes.Slice(20, jsonLength));
        var root = document.RootElement;
        var offset = 20 + jsonLength;
        var binaryLength = checked((int)ReadUInt32(span, offset));
        if (ReadUInt32(span, offset + 4) != 0x004e4942 || binaryLength < 0 || offset + 8 + binaryLength != span.Length)
        {
            throw new InvalidDataException("Invalid GLB binary chunk.");
        }

        var binary = span.Slice(offset + 8, binaryLength).ToArray();
        var buffers = root.GetProperty("buffers");
        if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _))
        {
            throw new InvalidDataException("GLB must carry its mesh buffer.");
        }

        var declaredLength = buffers[0].GetProperty("byteLength").GetInt32();
        if (declaredLength < 0 || declaredLength > binary.Length || binary.Length - declaredLength > 3)
        {
            throw new InvalidDataException("Invalid GLB buffer length.");
        }

        if (root.TryGetProperty("extensionsRequired", out var required))
        {
            foreach (var extension in required.EnumerateArray())
            {
                if (extension.GetString() is not ("EXT_mesh_features" or "EXT_structural_metadata"))
                {
                    throw new InvalidDataException("Unsupported required mesh extension.");
                }
            }
        }

        var reader = new SceneGlbAssetReader(root, binary, batchRows ?? ReadStructuralTable(root, binary), readRelativeAsset, cancellationToken);
        // Honua's pre-existing writer stores ECEF coordinates directly, as its schema documents;
        // imported glTF follows the standard Y-up to 3D Tiles Z-up conversion.
        var nativeEcef = root.TryGetProperty("extensions", out var extensions)
            && extensions.TryGetProperty("EXT_structural_metadata", out var metadata)
            && metadata.TryGetProperty("schema", out var schema)
            && schema.TryGetProperty("id", out var schemaId) && schemaId.GetString() == "honua_scene_schema";
        var axis = nativeEcef || string.Equals(upAxis, "Z", StringComparison.OrdinalIgnoreCase)
            ? SceneAffineTransform.Identity : SceneAffineTransform.YUpToZUp;
        if (upAxis is not null && !string.Equals(upAxis, "Z", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(upAxis, "Y", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unsupported glTF up axis.");
        }

        var rtcMatrix = (double[])SceneAffineTransform.Identity.Clone();
        (rtcMatrix[12], rtcMatrix[13], rtcMatrix[14]) = (rtc.X, rtc.Y, rtc.Z);
        var outer = SceneAffineTransform.Multiply(tileTransform, SceneAffineTransform.Multiply(rtcMatrix, axis));
        var scenes = root.GetProperty("scenes");
        var sceneIndex = root.TryGetProperty("scene", out var selectedScene) ? selectedScene.GetInt32() : 0;
        foreach (var node in scenes[sceneIndex].GetProperty("nodes").EnumerateArray())
        {
            reader.WalkNode(node.GetInt32(), outer, 0);
        }

        if (reader._triangles.Count == 0)
        {
            throw new InvalidDataException("Scene content has no renderable triangles.");
        }

        return reader._triangles.GroupBy(triangle => triangle.Material).Select(group =>
        {
            var material = group.Key >= 0 ? root.GetProperty("materials")[group.Key].Clone() : default;
            return new SceneDecodedMesh(group.ToArray(), reader._features, material, reader.ReadTextures(material));
        }).ToArray();
    }

    private void WalkNode(int index, double[] parent, int depth)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (depth > 128 || !_activeNodes.Add(index))
        {
            throw new InvalidDataException("Invalid glTF node graph.");
        }

        var node = _document.GetProperty("nodes")[index];
        var transform = SceneAffineTransform.Multiply(parent, SceneAffineTransform.Read(node, "matrix"));
        if (node.TryGetProperty("skin", out _) || node.TryGetProperty("weights", out _))
        {
            throw new InvalidDataException("Animated scene meshes require baking before publication.");
        }

        if (node.TryGetProperty("mesh", out var meshIndex))
        {
            foreach (var primitive in _document.GetProperty("meshes")[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                ReadPrimitive(primitive, transform);
            }
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                WalkNode(child.GetInt32(), transform, depth + 1);
            }
        }

        _activeNodes.Remove(index);
    }

    private void ReadPrimitive(JsonElement primitive, double[] transform)
    {
        var axisX = new SceneCartesian(transform[0], transform[1], transform[2]);
        var axisY = new SceneCartesian(transform[4], transform[5], transform[6]);
        var axisZ = new SceneCartesian(transform[8], transform[9], transform[10]);
        var cross = SceneCartesian.Cross(axisY, axisZ);
        var determinant = axisX.X * cross.X + axisX.Y * cross.Y + axisX.Z * cross.Z;
        if (!double.IsFinite(determinant) || determinant <= 0)
        {
            throw new InvalidDataException("Reflective or singular mesh transforms must be baked before publication.");
        }

        var mode = primitive.TryGetProperty("mode", out var encodedMode) ? encodedMode.GetInt32() : 4;
        if (mode is not (4 or 5 or 6) || primitive.TryGetProperty("targets", out _))
        {
            throw new InvalidDataException("Content is not a baked triangle mesh.");
        }

        var material = primitive.TryGetProperty("material", out var encodedMaterial) ? encodedMaterial.GetInt32() : -1;
        var attributes = primitive.GetProperty("attributes");
        if (_document.GetProperty("accessors")[attributes.GetProperty("POSITION").GetInt32()].GetProperty("componentType").GetInt32() != 5126)
        {
            throw new InvalidDataException("glTF positions must use Float32 components.");
        }
        var positions = ReadAccessor(attributes.GetProperty("POSITION").GetInt32(), 3);
        var count = positions.Length / 3;
        var featureIds = attributes.TryGetProperty("_FEATURE_ID_0", out var ids) || attributes.TryGetProperty("_BATCHID", out ids)
            ? ReadAccessor(ids.GetInt32(), 1) : new double[count];
        if (ids.ValueKind == JsonValueKind.Undefined && _features.Count != 1)
        {
            throw new InvalidDataException("A multi-feature mesh must identify its feature vertices.");
        }

        if (primitive.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("EXT_mesh_features", out var meshFeatures))
        {
            var definitions = meshFeatures.GetProperty("featureIds");
            if (definitions.GetArrayLength() != 1 || definitions[0].GetProperty("attribute").GetInt32() != 0
                || definitions[0].GetProperty("propertyTable").GetInt32() != 0
                || definitions[0].GetProperty("featureCount").GetInt32() != _features.Count)
            {
                throw new InvalidDataException("Mesh feature references do not match the persisted property table.");
            }
        }
        var uv = attributes.TryGetProperty("TEXCOORD_0", out var uvIndex) ? ReadAccessor(uvIndex.GetInt32(), 2) : new double[count * 2];
        var uv1 = attributes.TryGetProperty("TEXCOORD_1", out var uv1Index) ? ReadAccessor(uv1Index.GetInt32(), 2) : new double[count * 2];
        var colors = attributes.TryGetProperty("COLOR_0", out var colorIndex) ? ReadAccessor(colorIndex.GetInt32(), 0) : null;
        var normals = attributes.TryGetProperty("NORMAL", out var normalIndex) ? ReadAccessor(normalIndex.GetInt32(), 3) : null;
        if (featureIds.Length != count || uv.Length != count * 2 || uv1.Length != count * 2 || (normals is not null && normals.Length != count * 3) || (colors is not null && colors.Length != count * 3 && colors.Length != count * 4))
        {
            throw new InvalidDataException("Vertex attribute counts do not agree.");
        }

        if (primitive.TryGetProperty("indices", out var indexType)
            && _document.GetProperty("accessors")[indexType.GetInt32()].GetProperty("componentType").GetInt32() is not (5121 or 5123 or 5125))
        {
            throw new InvalidDataException("glTF indices must use unsigned integer components.");
        }

        var indices = primitive.TryGetProperty("indices", out var indexAccessor)
            ? ReadAccessor(indexAccessor.GetInt32(), 1) : Enumerable.Range(0, count).Select(i => (double)i).ToArray();
        if (indices.Length < 3 || (mode == 4 && indices.Length % 3 != 0))
        {
            throw new InvalidDataException("Invalid triangle indices.");
        }

        var faceCount = mode == 4 ? indices.Length / 3 : indices.Length - 2;
        if (checked((_triangles.Count + faceCount) * 3) > MaxVertices)
        {
            throw new InvalidDataException("Mesh exceeds the serving vertex budget.");
        }

        for (var face = 0; face < faceCount; face++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var a = mode == 4 ? face * 3 : mode == 6 ? 0 : face;
            var b = mode == 4 ? a + 1 : face + 1;
            var c = mode == 4 ? a + 2 : face + 2;
            if (mode == 5 && face % 2 == 1)
            {
                (a, b) = (b, a);
            }

            var ia = CheckedIndex(indices[a], count);
            var ib = CheckedIndex(indices[b], count);
            var ic = CheckedIndex(indices[c], count);
            var feature = CheckedIndex(featureIds[ia], _features.Count);
            if (featureIds[ib] != feature || featureIds[ic] != feature)
            {
                throw new InvalidDataException("A triangle crosses feature identifiers.");
            }

            var va = ReadVertex(ia);
            var vb = ReadVertex(ib);
            var vc = ReadVertex(ic);
            var normal = SceneCartesian.Cross(vb.Position - va.Position, vc.Position - va.Position);
            if (!normal.IsFinite || normal.Length < 1e-12)
            {
                throw new InvalidDataException("Degenerate mesh face.");
            }

            _triangles.Add(new(feature, va, vb, vc, normal / normal.Length, material));
        }

        SceneMeshVertex ReadVertex(int index)
        {
            var position = SceneAffineTransform.Apply(transform, new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]));
            if (!position.IsFinite)
            {
                throw new InvalidDataException("Non-finite mesh position.");
            }

            var colorCount = colors?.Length / count ?? 0;
            return new SceneMeshVertex(position, uv[index * 2], uv[index * 2 + 1], uv1[index * 2], uv1[index * 2 + 1],
                colors is null ? 1 : colors[index * colorCount], colors is null ? 1 : colors[index * colorCount + 1],
                colors is null ? 1 : colors[index * colorCount + 2], colorCount == 4 ? colors![index * colorCount + 3] : 1,
                normals is null ? null : SceneAffineTransform.ApplyNormal(transform, new(normals[index * 3], normals[index * 3 + 1], normals[index * 3 + 2])));
        }
    }

    private double[] ReadAccessor(int index, int expectedComponents)
    {
        var accessor = _document.GetProperty("accessors")[index];
        if (accessor.TryGetProperty("sparse", out _))
        {
            throw new InvalidDataException("Sparse accessors require baking.");
        }

        var components = accessor.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new InvalidDataException("Unsupported vertex accessor.")
        };
        var count = accessor.GetProperty("count").GetInt32();
        if (count <= 0 || count > MaxVertices || (expectedComponents != 0 && expectedComponents != components))
        {
            throw new InvalidDataException("Invalid vertex accessor dimensions.");
        }

        var view = _document.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var bytes = ReadView(_document, _binary, accessor.GetProperty("bufferView").GetInt32());
        var component = accessor.GetProperty("componentType").GetInt32();
        var width = ComponentWidth(component);
        var stride = view.TryGetProperty("byteStride", out var encodedStride) ? encodedStride.GetInt32() : width * components;
        var offset = accessor.TryGetProperty("byteOffset", out var encodedOffset) ? encodedOffset.GetInt32() : 0;
        if (stride < width * components || stride % width != 0 || offset < 0 || checked((long)offset + (long)(count - 1) * stride + components * width) > bytes.Length)
        {
            throw new InvalidDataException("Accessor exceeds its buffer view.");
        }

        var normalized = accessor.TryGetProperty("normalized", out var encodedNormalized) && encodedNormalized.GetBoolean();
        var result = new double[checked(count * components)];
        for (var i = 0; i < count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            for (var c = 0; c < components; c++)
            {
                var value = ReadComponent(bytes, offset + i * stride + c * width, component);
                if (normalized)
                {
                    value = component switch
                    {
                        5120 => Math.Max(-1, value / 127),
                        5121 => value / 255,
                        5122 => Math.Max(-1, value / 32767),
                        5123 => value / 65535,
                        _ => throw new InvalidDataException("Invalid normalized component type.")
                    };
                }

                if (!double.IsFinite(value))
                {
                    throw new InvalidDataException("Non-finite mesh component.");
                }

                result[i * components + c] = value;
            }
        }

        return result;
    }

    private Dictionary<string, SceneTextureBinding> ReadTextures(JsonElement material)
    {
        var result = new Dictionary<string, SceneTextureBinding>(StringComparer.Ordinal);
        if (material.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            Add(pbr, "baseColorTexture", "0");
            Add(pbr, "metallicRoughnessTexture", "1");
        }

        Add(material, "normalTexture", "2");
        Add(material, "occlusionTexture", "3");
        Add(material, "emissiveTexture", "4");
        return result;

        void Add(JsonElement owner, string name, string key)
        {
            if (!owner.TryGetProperty(name, out var texture))
            {
                return;
            }

            if (texture.TryGetProperty("extensions", out _))
            {
                throw new InvalidDataException("Texture transforms must be baked into the mesh UVs.");
            }

            var coordinate = texture.TryGetProperty("texCoord", out var uv) ? uv.GetInt32() : 0;
            if (coordinate is not (0 or 1))
            {
                throw new InvalidDataException("Unsupported texture coordinate set.");
            }

            var encodedTexture = _document.GetProperty("textures")[texture.GetProperty("index").GetInt32()];
            if (encodedTexture.TryGetProperty("sampler", out var samplerIndex))
            {
                var sampler = _document.GetProperty("samplers")[samplerIndex.GetInt32()];
                if (sampler.TryGetProperty("wrapS", out var wrapS) && wrapS.GetInt32() != 10497
                    || sampler.TryGetProperty("wrapT", out var wrapT) && wrapT.GetInt32() != 10497)
                {
                    throw new InvalidDataException("Non-repeating texture addressing must be baked into mesh UVs.");
                }
            }

            var source = encodedTexture.GetProperty("source").GetInt32();
            var image = _document.GetProperty("images")[source];
            byte[] data;
            if (image.TryGetProperty("bufferView", out var view))
            {
                data = ReadView(_document, _binary, view.GetInt32()).ToArray();
            }
            else
            {
                var uri = image.GetProperty("uri").GetString() ?? throw new InvalidDataException("Missing texture URI.");
                var comma = uri.IndexOf(',', StringComparison.Ordinal);
                if (uri.StartsWith("data:image/", StringComparison.Ordinal) && comma > 0 && uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                {
                    if (uri.Length - comma - 1 > 4 * ((8 * 1024 * 1024 + 2) / 3))
                    {
                        throw new InvalidDataException("Embedded texture exceeds the serving byte budget.");
                    }

                    data = Convert.FromBase64String(uri[(comma + 1)..]);
                }
                else
                {
                    data = _readRelativeAsset(uri);
                }
            }

            var png = data.Length >= 33 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var jpeg = data.Length >= 4 && data[0] == 255 && data[1] == 216 && data[^2] == 255 && data[^1] == 217;
            if ((!png && !jpeg) || data.Length > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("Texture is not a bounded PNG or JPEG image.");
            }

            if (png) { ValidatePng(data); }

            var factor = texture.TryGetProperty("scale", out var scale) ? scale.GetDouble()
                : texture.TryGetProperty("strength", out var strength) ? strength.GetDouble() : (double?)null;
            if (factor is { } actual && !double.IsFinite(actual))
            {
                throw new InvalidDataException("Invalid material texture factor.");
            }

            result[key] = new(new(data, png ? "image/png" : "image/jpeg", png ? "png" : "jpg"), coordinate, factor);
        }
    }

    private static void ValidatePng(byte[] bytes)
    {
        var offset = 8;
        var first = true;
        var hasData = false;
        var ended = false;
        while (offset <= bytes.Length - 12)
        {
            var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
            if (count < 0 || count > bytes.Length - offset - 12) { throw new InvalidDataException("Invalid PNG chunk range."); }
            var type = bytes.AsSpan(offset + 4, 4);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || count != 13) { throw new InvalidDataException("Invalid PNG image header."); }
                var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8));
                var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12));
                if (width == 0 || height == 0 || width > 8192 || height > 8192) { throw new InvalidDataException("Texture dimensions exceed the serving budget."); }
                first = false;
            }

            var crc = uint.MaxValue;
            foreach (var value in bytes.AsSpan(offset + 4, count + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0); }
            }

            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + count + 8)))
            { throw new InvalidDataException("Invalid PNG chunk checksum."); }
            hasData |= type.SequenceEqual("IDAT"u8) && count > 0;
            ended = type.SequenceEqual("IEND"u8) && count == 0;
            offset += count + 12;
            if (ended) { break; }
        }

        if (!hasData || !ended || offset != bytes.Length) { throw new InvalidDataException("Incomplete PNG texture."); }
    }

    private static IReadOnlyList<SceneCachedFeature> ReadBatchTable(JsonElement batch, byte[] binary, int count)
    {
        if (count == 0)
        {
            return [new(0, new Dictionary<string, object?>())];
        }

        var rows = Enumerable.Range(0, count).Select(_ => new Dictionary<string, object?>(StringComparer.Ordinal)).ToArray();
        if (batch.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in batch.EnumerateObject())
            {
                if (property.Name is "extensions" or "extras")
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    if (property.Value.GetArrayLength() != count)
                    {
                        throw new InvalidDataException("Batch property count does not match geometry.");
                    }

                    for (var i = 0; i < count; i++)
                    {
                        rows[i][property.Name] = Scalar(property.Value[i]);
                    }
                }
                else
                {
                    var value = property.Value;
                    if (value.GetProperty("type").GetString() != "SCALAR")
                    {
                        throw new InvalidDataException("Only scalar batch attributes are supported.");
                    }

                    var component = BatchComponent(value.GetProperty("componentType").GetString());
                    var start = value.GetProperty("byteOffset").GetInt32();
                    for (var i = 0; i < count; i++)
                    {
                        rows[i][property.Name] = ReadComponent(binary, checked(start + i * ComponentWidth(component)), component);
                    }
                }
            }
        }

        return Features(rows);
    }

    private static IReadOnlyList<SceneCachedFeature> ReadStructuralTable(JsonElement root, byte[] binary)
    {
        if (!root.TryGetProperty("extensions", out var extensions) || !extensions.TryGetProperty("EXT_structural_metadata", out var metadata))
        {
            return [new(0, new Dictionary<string, object?>())];
        }

        var tables = metadata.GetProperty("propertyTables");
        if (tables.GetArrayLength() != 1)
        {
            throw new InvalidDataException("Mesh metadata must have one canonical feature table.");
        }

        var table = tables[0];
        var count = table.GetProperty("count").GetInt32();
        if (count <= 0 || count > MaxVertices)
        {
            throw new InvalidDataException("Invalid feature table count.");
        }

        var rows = Enumerable.Range(0, count).Select(_ => new Dictionary<string, object?>(StringComparer.Ordinal)).ToArray();
        var schema = metadata.GetProperty("schema").GetProperty("classes").GetProperty(table.GetProperty("class").GetString()!).GetProperty("properties");
        foreach (var definition in schema.EnumerateObject())
        {
            RejectMetadataModifiers(definition.Value);
        }

        foreach (var property in table.GetProperty("properties").EnumerateObject())
        {
            RejectMetadataModifiers(property.Value);
            var definition = schema.GetProperty(property.Name);
            if (definition.TryGetProperty("array", out var array) && array.GetBoolean())
            {
                throw new InvalidDataException("Only scalar feature attributes are supported.");
            }

            var values = ReadView(root, binary, property.Value.GetProperty("values").GetInt32());
            var type = definition.GetProperty("type").GetString();
            if (type == "STRING")
            {
                var offsets = ReadView(root, binary, property.Value.GetProperty("stringOffsets").GetInt32());
                var offsetType = property.Value.TryGetProperty("stringOffsetType", out var encodedType) ? encodedType.GetString() : "UINT32";
                var width = offsetType == "UINT64" ? 8 : offsetType == "UINT32" ? 4 : throw new InvalidDataException("Unsupported string offset type.");
                for (var row = 0; row < count; row++)
                {
                    var start = checked((int)ReadUnsigned(offsets, row * width, width));
                    var end = checked((int)ReadUnsigned(offsets, (row + 1) * width, width));
                    if (start < 0 || end < start || end > values.Length)
                    {
                        throw new InvalidDataException("String offsets exceed the property buffer.");
                    }

                    rows[row][property.Name] = new UTF8Encoding(false, true).GetString(values.Slice(start, end - start));
                }
            }
            else if (type == "BOOLEAN")
            {
                for (var row = 0; row < count; row++)
                {
                    rows[row][property.Name] = (values[row / 8] & (1 << (row % 8))) != 0;
                }
            }
            else if (type == "SCALAR")
            {
                var component = definition.GetProperty("componentType").GetString();
                var width = StructuralWidth(component);
                for (var row = 0; row < count; row++)
                {
                    rows[row][property.Name] = ReadStructuralScalar(values, row * width, component);
                }
            }
            else
            {
                throw new InvalidDataException("Unsupported feature attribute type.");
            }
        }

        return Features(rows);
    }

    private static void RejectMetadataModifiers(JsonElement declaration)
    {
        // Publishing raw values for these declarations would silently change the
        // source's meaning. This serving profile requires them to be baked.
        foreach (var name in new[] { "normalized", "offset", "scale", "noData", "default" })
        {
            if (declaration.TryGetProperty(name, out _))
            {
                throw new InvalidDataException("Transformed or defaulted metadata must be baked before publication.");
            }
        }
    }

    private static List<SceneCachedFeature> Features(Dictionary<string, object?>[] rows)
    {
        var features = new List<SceneCachedFeature>(rows.Length);
        var identifiers = new HashSet<long>();
        for (var i = 0; i < rows.Length; i++)
        {
            var key = rows[i].Keys.FirstOrDefault(name => string.Equals(name, "OBJECTID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "id", StringComparison.OrdinalIgnoreCase));
            var raw = key is null ? (object)i : rows[i][key];
            var id = raw switch
            {
                int value => value,
                long value => value,
                ulong value when value <= int.MaxValue => (long)value,
                double value when double.IsFinite(value) && value >= 0 && value <= int.MaxValue && value == Math.Truncate(value) => (long)value,
                _ => throw new InvalidDataException("Invalid source feature identifier.")
            };
            if (id < 0 || id > int.MaxValue || !identifiers.Add(id))
            {
                throw new InvalidDataException("Feature identifiers must be unique nonnegative Oid32 values.");
            }

            if (key is not null)
            {
                rows[i].Remove(key);
            }

            features.Add(new(id, rows[i]));
        }

        return features;
    }

    internal static ReadOnlySpan<byte> ReadView(JsonElement root, byte[] binary, int index)
    {
        var view = root.GetProperty("bufferViews")[index];
        if (view.GetProperty("buffer").GetInt32() != 0)
        {
            throw new InvalidDataException("External mesh buffers require baking.");
        }

        var offset = view.TryGetProperty("byteOffset", out var start) ? start.GetInt32() : 0;
        var length = view.GetProperty("byteLength").GetInt32();
        if (offset < 0 || length < 0 || (long)offset + length > binary.Length)
        {
            throw new InvalidDataException("Buffer view exceeds the persisted asset.");
        }

        return binary.AsSpan(offset, length);
    }

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        _ => throw new InvalidDataException("Batch attributes must be scalar values.")
    };

    private static int CheckedIndex(double value, int count) => value >= 0 && value < count && value == Math.Truncate(value)
        ? (int)value : throw new InvalidDataException("Invalid mesh or feature index.");
    private static int ComponentWidth(int type) => type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException("Unsupported component type.") };
    private static int BatchComponent(string? type) => type switch { "BYTE" => 5120, "UNSIGNED_BYTE" => 5121, "SHORT" => 5122, "UNSIGNED_SHORT" => 5123, "UNSIGNED_INT" => 5125, "FLOAT" => 5126, _ => throw new InvalidDataException("Unsupported batch component type.") };
    private static int StructuralWidth(string? type) => type switch { "INT8" or "UINT8" => 1, "INT16" or "UINT16" => 2, "INT32" or "UINT32" or "FLOAT32" => 4, "INT64" or "UINT64" or "FLOAT64" => 8, _ => throw new InvalidDataException("Unsupported feature scalar type.") };
    // Explicit boxing prevents the numeric switch's common type from promoting
    // integer values to double and losing Int64 precision above 2^53.
    private static object ReadStructuralScalar(ReadOnlySpan<byte> bytes, int offset, string? type) => type switch
    {
        "INT8" => (object)(long)(sbyte)bytes[offset],
        "UINT8" => (object)(long)bytes[offset],
        "INT16" => (object)(long)BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]),
        "UINT16" => (object)(long)BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
        "INT32" => (object)(long)BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]),
        "UINT32" => (object)(long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]),
        "INT64" => (object)BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]),
        "UINT64" => (object)BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]),
        "FLOAT32" => (double)BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
        "FLOAT64" => BinaryPrimitives.ReadDoubleLittleEndian(bytes[offset..]),
        _ => throw new InvalidDataException("Unsupported feature scalar type.")
    };
    private static double ReadComponent(ReadOnlySpan<byte> bytes, int offset, int type) => type switch
    {
        5120 => (sbyte)bytes[offset],
        5121 => bytes[offset],
        5122 => BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]),
        5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
        5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]),
        5126 => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
        _ => throw new InvalidDataException("Unsupported component type.")
    };
    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes, int offset, int width) => width == 8
        ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}

internal sealed record SceneCachedFeature(long Id, IReadOnlyDictionary<string, object?> Attributes);
internal readonly record struct SceneMeshVertex(SceneCartesian Position, double U, double V, double U1, double V1, double R, double G, double B, double A, SceneCartesian? Normal);
internal sealed record SceneMeshTriangle(int Feature, SceneMeshVertex A, SceneMeshVertex B, SceneMeshVertex C, SceneCartesian Normal, int Material);
internal sealed record SceneTextureBinding(I3sTextureResource Resource, int TexCoord, double? Factor);
internal sealed record SceneDecodedMesh(IReadOnlyList<SceneMeshTriangle> Triangles, IReadOnlyList<SceneCachedFeature> Features, JsonElement Material, IReadOnlyDictionary<string, SceneTextureBinding> Textures);
