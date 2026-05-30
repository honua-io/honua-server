// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Scene;

/// <summary>
/// MIME-type mapping for OGC 3D Tiles asset payloads.
/// </summary>
/// <remarks>
/// The 3D Tiles specification leaves binary content types unspecified, so we
/// follow Cesium's convention of <c>application/octet-stream</c> for the tile
/// container formats and use the registered <c>model/gltf-*</c> media types
/// for glTF/GLB payloads. Image and JSON payloads use their standard types.
/// </remarks>
internal static class SceneContentTypes
{
    /// <summary>
    /// Default content type for unknown extensions or extensionless files.
    /// </summary>
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>
    /// JSON content type used for <c>tileset.json</c> and nested tileset
    /// references.
    /// </summary>
    public const string JsonContentType = "application/json";

    /// <summary>
    /// USDA text-format OpenUSD stage manifest content type.
    /// </summary>
    public const string OpenUsdStageContentType = "model/vnd.usda";

    /// <summary>
    /// Resolves the MIME type for a 3D Tiles asset by its filename or path.
    /// </summary>
    /// <param name="path">Asset path or filename.</param>
    /// <returns>The matching media type, or <see cref="DefaultContentType"/> when unknown.</returns>
    public static string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return DefaultContentType;
        }

        var extension = Path.GetExtension(path.AsSpan());
        if (extension.IsEmpty)
        {
            return DefaultContentType;
        }

        return extension switch
        {
            _ when extension.Equals(".json", StringComparison.OrdinalIgnoreCase) => JsonContentType,
            _ when extension.Equals(".b3dm", StringComparison.OrdinalIgnoreCase) => DefaultContentType,
            _ when extension.Equals(".i3dm", StringComparison.OrdinalIgnoreCase) => DefaultContentType,
            _ when extension.Equals(".pnts", StringComparison.OrdinalIgnoreCase) => DefaultContentType,
            _ when extension.Equals(".cmpt", StringComparison.OrdinalIgnoreCase) => DefaultContentType,
            _ when extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) => "model/gltf-binary",
            _ when extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) => "model/gltf+json",
            _ when extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) => DefaultContentType,
            _ when extension.Equals(".png", StringComparison.OrdinalIgnoreCase) => "image/png",
            _ when extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) => "image/jpeg",
            _ when extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) => "image/jpeg",
            _ when extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) => "image/webp",
            _ when extension.Equals(".ktx", StringComparison.OrdinalIgnoreCase) => "image/ktx",
            _ when extension.Equals(".ktx2", StringComparison.OrdinalIgnoreCase) => "image/ktx2",
            _ when extension.Equals(".basis", StringComparison.OrdinalIgnoreCase) => "image/basis",
            _ => DefaultContentType
        };
    }
}
