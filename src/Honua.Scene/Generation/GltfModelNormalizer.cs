// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;

namespace Honua.Core.Features.Scene.Generation;

internal static class GltfModelNormalizer
{
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbVersion = 2;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinChunkType = 0x004E4942;

    public static NormalizedModelResult NormalizeToGlb(
        byte[] sourceBytes,
        string fileName,
        string? generatorTag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Model payload must not be empty.");
        }

        var format = DetectFormat(sourceBytes, fileName);
        return format switch
        {
            "obj" => NormalizeObj(sourceBytes, generatorTag),
            "gltf" => NormalizeEmbeddedGltf(sourceBytes, cancellationToken),
            "glb" => ValidateGlb(sourceBytes, cancellationToken),
            _ => throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Unsupported model format for '{fileName}'. Supported formats: .glb, .gltf (embedded), .obj."),
        };
    }

    public static double[] BuildPlacementTransform(ModelPlacement placement)
    {
        ValidatePlacement(placement);

        var lonRad = placement.Longitude * Math.PI / 180.0;
        var latRad = placement.Latitude * Math.PI / 180.0;
        var sinLon = Math.Sin(lonRad);
        var cosLon = Math.Cos(lonRad);
        var sinLat = Math.Sin(latRad);
        var cosLat = Math.Cos(latRad);
        var (tx, ty, tz) = EcefCoordinateTransform.ToEcef(placement.Longitude, placement.Latitude, placement.Height);

        // Column-major WGS-84 East-North-Up to ECEF matrix.
        double[] enuToEcef =
        [
            -sinLon, cosLon, 0, 0,
            -sinLat * cosLon, -sinLat * sinLon, cosLat, 0,
            cosLat * cosLon, cosLat * sinLon, sinLat, 0,
            tx, ty, tz, 1,
        ];

        var heading = -placement.HeadingDegrees * Math.PI / 180.0;
        var pitch = placement.PitchDegrees * Math.PI / 180.0;
        var roll = placement.RollDegrees * Math.PI / 180.0;

        double[] rotZ =
        [
            Math.Cos(heading), Math.Sin(heading), 0, 0,
            -Math.Sin(heading), Math.Cos(heading), 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];

        double[] rotX =
        [
            1, 0, 0, 0,
            0, Math.Cos(pitch), Math.Sin(pitch), 0,
            0, -Math.Sin(pitch), Math.Cos(pitch), 0,
            0, 0, 0, 1,
        ];

        double[] rotY =
        [
            Math.Cos(roll), 0, -Math.Sin(roll), 0,
            0, 1, 0, 0,
            Math.Sin(roll), 0, Math.Cos(roll), 0,
            0, 0, 0, 1,
        ];

        double[] scale =
        [
            placement.ScaleX, 0, 0, 0,
            0, placement.ScaleY, 0, 0,
            0, 0, placement.ScaleZ, 0,
            0, 0, 0, 1,
        ];

        var local = SceneAffineTransform.Multiply(rotZ, SceneAffineTransform.Multiply(rotX, SceneAffineTransform.Multiply(rotY, scale)));
        return SceneAffineTransform.Multiply(enuToEcef, local);
    }

    public static (double[] BoundsDegrees, double MinHeight, double MaxHeight) ComputeGeographicBounds(
        ModelLocalBounds gltfLocalBounds,
        double[] tileTransform)
    {
        // glTF assets are Y-up; 3D Tiles applies Y-up -> Z-up before the tile transform.
        var worldFromGltf = SceneAffineTransform.Multiply(tileTransform, SceneAffineTransform.YUpToZUp);
        double[] xs = [gltfLocalBounds.MinX, gltfLocalBounds.MaxX];
        double[] ys = [gltfLocalBounds.MinY, gltfLocalBounds.MaxY];
        double[] zs = [gltfLocalBounds.MinZ, gltfLocalBounds.MaxZ];

        double minLon = double.PositiveInfinity, minLat = double.PositiveInfinity;
        double maxLon = double.NegativeInfinity, maxLat = double.NegativeInfinity;
        double minHeight = double.PositiveInfinity, maxHeight = double.NegativeInfinity;

        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                foreach (var z in zs)
                {
                    var ecef = SceneAffineTransform.Apply(worldFromGltf, new SceneCartesian(x, y, z));
                    var (lon, lat, height) = EcefCoordinateTransform.FromEcef(ecef.X, ecef.Y, ecef.Z);
                    if (lon < minLon) minLon = lon;
                    if (lat < minLat) minLat = lat;
                    if (lon > maxLon) maxLon = lon;
                    if (lat > maxLat) maxLat = lat;
                    if (height < minHeight) minHeight = height;
                    if (height > maxHeight) maxHeight = height;
                }
            }
        }

        return ([minLon, minLat, maxLon, maxLat], minHeight, maxHeight);
    }

    public static void ValidatePlacement(ModelPlacement placement)
    {
        if (!double.IsFinite(placement.Longitude) || placement.Longitude < -180.0 || placement.Longitude > 180.0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: placement longitude must be between -180 and 180.");
        }

        if (!double.IsFinite(placement.Latitude) || placement.Latitude < -90.0 || placement.Latitude > 90.0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: placement latitude must be between -90 and 90.");
        }

        if (!double.IsFinite(placement.Height))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: placement height must be finite.");
        }

        if (!double.IsFinite(placement.HeadingDegrees)
            || !double.IsFinite(placement.PitchDegrees)
            || !double.IsFinite(placement.RollDegrees))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: placement orientation angles must be finite.");
        }

        if (!double.IsFinite(placement.ScaleX) || placement.ScaleX <= 0
            || !double.IsFinite(placement.ScaleY) || placement.ScaleY <= 0
            || !double.IsFinite(placement.ScaleZ) || placement.ScaleZ <= 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.OptionsInvalid}: placement scale components must be positive finite numbers.");
        }
    }

    private static string DetectFormat(byte[] sourceBytes, string fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (ext.Equals(".glb", StringComparison.OrdinalIgnoreCase)) return "glb";
        if (ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)) return "gltf";
        if (ext.Equals(".obj", StringComparison.OrdinalIgnoreCase)) return "obj";

        if (sourceBytes.Length >= 4 && sourceBytes.AsSpan(0, 4).SequenceEqual("glTF"u8))
        {
            return "glb";
        }

        var trimmed = sourceBytes.AsSpan();
        while (!trimmed.IsEmpty && trimmed[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            trimmed = trimmed[1..];
        }

        return !trimmed.IsEmpty && trimmed[0] == (byte)'{' ? "gltf" : "unknown";
    }

    private static NormalizedModelResult NormalizeObj(byte[] sourceBytes, string? generatorTag)
    {
        var converted = ObjToGlbConverter.Convert(sourceBytes, generatorTag);
        return new NormalizedModelResult(converted.GlbBytes, "obj", converted.Bounds);
    }

    private static NormalizedModelResult NormalizeEmbeddedGltf(byte[] sourceBytes, CancellationToken cancellationToken)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(sourceBytes);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Embedded glTF payload is not valid JSON.",
                ex);
        }

        if (rootNode is not JsonObject rootObj)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Embedded glTF root must be a JSON object.");
        }

        if (rootObj["buffers"] is not JsonArray buffers || buffers.Count == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Embedded glTF must define at least one buffer.");
        }

        if (buffers.Count != 1)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Embedded glTF with multiple buffers is not supported; pack into a single embedded buffer or GLB.");
        }

        var bufferObj = buffers[0] as JsonObject
            ?? throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: glTF buffer entry must be an object.");

        var uri = bufferObj["uri"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uri) || !TryDecodeBase64DataUri(uri, out var binaryBuffer))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: glTF buffer must be an embedded base64 data URI; external buffer URIs are not permitted.");
        }

        ValidateEmbeddedImages(rootObj);
        bufferObj.Remove("uri");
        bufferObj["byteLength"] = binaryBuffer.Length;

        var rawJson = Encoding.UTF8.GetBytes(rootObj.ToJsonString());
        var paddedJsonLen = (rawJson.Length + 3) / 4 * 4;
        var paddedBinLen = (binaryBuffer.Length + 3) / 4 * 4;
        var totalLength = 12 + 8 + paddedJsonLen + 8 + paddedBinLen;
        var glb = new byte[totalLength];

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)totalLength);

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)paddedJsonLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), JsonChunkType);
        glb.AsSpan(20, paddedJsonLen).Fill(0x20);
        rawJson.CopyTo(glb, 20);

        var binOffset = 20 + paddedJsonLen;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binOffset, 4), (uint)paddedBinLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binOffset + 4, 4), BinChunkType);
        binaryBuffer.CopyTo(glb, binOffset + 8);

        var validated = ValidateGlb(glb, cancellationToken);
        return validated with { SourceFormat = "gltf" };
    }

    private static void ValidateEmbeddedImages(JsonObject rootObj)
    {
        if (rootObj["images"] is not JsonArray images)
        {
            return;
        }

        foreach (var item in images)
        {
            if (item is JsonObject imageObj && imageObj["uri"] is JsonValue uriNode)
            {
                var uri = uriNode.GetValue<string>();
                if (!uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.ModelAssetInvalid}: External image URI '{uri}' is not permitted; embed images in the glTF/GLB.");
                }
            }
        }
    }

    private static bool TryDecodeBase64DataUri(string uri, out byte[] bytes)
    {
        bytes = [];
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(uri[(comma + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static NormalizedModelResult ValidateGlb(byte[] glbBytes, CancellationToken cancellationToken)
    {
        // Decode with the production SceneGlbAssetReader in Z-up identity space so we prove the
        // GLB is structurally sound, self-contained, and renderable by both 3D Tiles and I3S pipelines.
        IReadOnlyList<SceneDecodedMesh> meshes;
        try
        {
            meshes = SceneGlbAssetReader.Read(
                glbBytes,
                SceneAffineTransform.Identity,
                upAxis: "Z",
                _ => throw new InvalidDataException("External relative assets are not permitted in single-file model ingest."),
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or FormatException or OverflowException or IndexOutOfRangeException)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Model GLB validation failed: {ex.Message}",
                ex);
        }

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (var mesh in meshes)
        {
            foreach (var triangle in mesh.Triangles)
            {
                Expand(triangle.A.Position, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                Expand(triangle.B.Position, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                Expand(triangle.C.Position, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            }
        }

        var bounds = new ModelLocalBounds(minX, minY, minZ, maxX, maxY, maxZ);
        return new NormalizedModelResult(glbBytes, "glb", bounds);
    }

    private static void Expand(
        SceneCartesian point,
        ref double minX,
        ref double minY,
        ref double minZ,
        ref double maxX,
        ref double maxY,
        ref double maxZ)
    {
        if (point.X < minX) minX = point.X;
        if (point.Y < minY) minY = point.Y;
        if (point.Z < minZ) minZ = point.Z;
        if (point.X > maxX) maxX = point.X;
        if (point.Y > maxY) maxY = point.Y;
        if (point.Z > maxZ) maxZ = point.Z;
    }
}

public sealed record ModelPlacement(
    double Longitude,
    double Latitude,
    double Height = 0.0,
    double HeadingDegrees = 0.0,
    double PitchDegrees = 0.0,
    double RollDegrees = 0.0,
    double ScaleX = 1.0,
    double ScaleY = 1.0,
    double ScaleZ = 1.0);

internal sealed record NormalizedModelResult(
    byte[] GlbBytes,
    string SourceFormat,
    ModelLocalBounds LocalBounds);
