// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Scene.Assets;

internal static class SceneContentTypes
{
    public const string DefaultContentType = "application/octet-stream";

    public const string JsonContentType = "application/json";

    public const string OpenUsdStageContentType = "model/vnd.usda";

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
