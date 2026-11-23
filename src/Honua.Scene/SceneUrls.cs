// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Scene;

public static class SceneUrls
{
    private const string ScenePrefix = "/scenes/";
    private const string SceneApiPrefix = "/api/scenes/";
    private const string TilesetSuffix = "/tileset.json";
    private const string CopcSuffix = "/copc";
    private const string AccessEnvelopeSuffix = "/access-envelope";
    private const string ResolveSuffix = "/resolve";

    public static string TilesetRelativePath(string sceneId)
        => string.Create(CultureInfo.InvariantCulture, $"{ScenePrefix}{sceneId}{TilesetSuffix}");

    public static string AbsoluteTilesetUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, ScenePrefix, sceneId, TilesetSuffix);

    public static string AbsoluteCopcUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, ScenePrefix, sceneId, CopcSuffix);

    public static string AbsoluteAccessEnvelopeUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, ScenePrefix, sceneId, AccessEnvelopeSuffix);

    public static string AbsoluteSceneUrl(string baseUrl, string sceneId, string suffix)
        => BuildAbsolute(baseUrl, ScenePrefix, sceneId, suffix);

    public static string AbsoluteSceneApiUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, SceneApiPrefix, sceneId, suffix: null);

    public static string AbsoluteSceneResolveUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, SceneApiPrefix, sceneId, ResolveSuffix);

    public static string SceneAssetPathPrefix(string sceneId)
        => string.Create(CultureInfo.InvariantCulture, $"{ScenePrefix}{Uri.EscapeDataString(sceneId)}/");

    private static string BuildAbsolute(string baseUrl, string prefix, string sceneId, string? suffix)
    {
        var escapedSceneId = Uri.EscapeDataString(sceneId);
        return string.Concat(
            baseUrl.AsSpan().TrimEnd('/'),
            prefix.AsSpan(),
            escapedSceneId.AsSpan(),
            suffix.AsSpan());
    }
}
