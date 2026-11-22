// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.PointCloud;

namespace Honua.Server.Features.Admin;

internal static partial class SceneDatasetEndpoints
{
    private static (int StatusCode, string Error)? ValidateCopcSource(SceneDatasetRecord record)
    {
        if (SceneServingProjection.ResolveServingFormat(record) != SceneServingFormat.Copc)
        {
            return null;
        }

        var fileName = record.TilesetFileName;
        var candidatePath = Path.Join(record.AssetRoot, fileName);
        if (File.Exists(candidatePath))
        {
            return ValidateCopcFile(candidatePath, fileName);
        }

        if (fileName.EndsWith(".las", StringComparison.OrdinalIgnoreCase)
            || (fileName.EndsWith(".laz", StringComparison.OrdinalIgnoreCase)
                && !SceneServingProjection.IsCopcFileName(fileName)))
        {
            return (StatusCodes.Status409Conflict,
                "Untiled LAS/LAZ sources cannot be registered for direct COPC serving without conversion to .copc.laz or 3D Tiles.");
        }

        return null;
    }

    private static (int StatusCode, string Error)? ValidateCopcFile(string path, string fileName)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileLength = stream.Length;
        var header = new byte[(int)Math.Min(fileLength, 65_536)];
        var bytesRead = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var prefix = header.AsSpan(0, bytesRead);

        if (LasPointCloudReader.TryReadCopcInfo(prefix, out var info, out var copcError))
        {
            return info.TryValidateFileLength(fileLength, out var lengthError)
                ? null
                : (StatusCodes.Status400BadRequest, $"Invalid COPC file '{fileName}': {lengthError}");
        }

        if (PointCloudCompressionDetector.HasLasfSignature(prefix))
        {
            return (StatusCodes.Status409Conflict,
                $"Untiled LAS/LAZ cannot be registered for direct COPC serving without conversion: {copcError}");
        }

        return (StatusCodes.Status400BadRequest, $"Invalid COPC file '{fileName}': {copcError}");
    }
}
