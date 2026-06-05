// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Scene;

/// <summary>
/// Single source of truth for the scene asset/discovery URL contract so the
/// 3D Tiles asset surface, the HTTP discovery surface, and the gRPC scene
/// metadata stay in lockstep. If the scene route prefix or the tileset filename
/// ever changes it changes here once instead of drifting across the call sites
/// that emit client-facing links (CLAUDE.md DRY rule for behaviour-bearing
/// link/URL shape).
/// </summary>
public static class SceneUrls
{
    private const string ScenePrefix = "/scenes/";
    private const string SceneApiPrefix = "/api/scenes/";
    private const string TilesetSuffix = "/tileset.json";
    private const string ResolveSuffix = "/resolve";

    /// <summary>
    /// The server-relative path at which a scene's <c>tileset.json</c> is
    /// served (<c>/scenes/{sceneId}/tileset.json</c>). The scene id is inserted
    /// verbatim (no URL escaping) to match the gRPC metadata contract; callers
    /// emitting absolute, client-dereferenceable links use
    /// <see cref="AbsoluteTilesetUrl"/> instead.
    /// </summary>
    /// <param name="sceneId">Stable scene identifier.</param>
    public static string TilesetRelativePath(string sceneId)
        => string.Create(CultureInfo.InvariantCulture, $"{ScenePrefix}{sceneId}{TilesetSuffix}");

    /// <summary>
    /// Absolute URL to a scene's <c>tileset.json</c>
    /// (<c>{baseUrl}/scenes/{escapedSceneId}/tileset.json</c>). The scene id is
    /// percent-escaped so the link is safe to hand to a client.
    /// </summary>
    /// <param name="baseUrl">Origin/base URL; a single trailing slash is trimmed.</param>
    /// <param name="sceneId">Stable scene identifier.</param>
    public static string AbsoluteTilesetUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, ScenePrefix, sceneId, TilesetSuffix);

    /// <summary>
    /// Absolute URL to a scene asset under the scene route
    /// (<c>{baseUrl}/scenes/{escapedSceneId}{suffix}</c>).
    /// </summary>
    /// <param name="baseUrl">Origin/base URL; a single trailing slash is trimmed.</param>
    /// <param name="sceneId">Stable scene identifier.</param>
    /// <param name="suffix">Trailing path segment (e.g. <c>/tileset.json</c>); may be empty.</param>
    public static string AbsoluteSceneUrl(string baseUrl, string sceneId, string suffix)
        => BuildAbsolute(baseUrl, ScenePrefix, sceneId, suffix);

    /// <summary>
    /// Absolute URL to a scene's metadata document under the scene API route
    /// (<c>{baseUrl}/api/scenes/{escapedSceneId}</c>).
    /// </summary>
    /// <param name="baseUrl">Origin/base URL; a single trailing slash is trimmed.</param>
    /// <param name="sceneId">Stable scene identifier.</param>
    public static string AbsoluteSceneApiUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, SceneApiPrefix, sceneId, suffix: null);

    /// <summary>
    /// Absolute URL to a scene's access-resolve endpoint under the scene API
    /// route (<c>{baseUrl}/api/scenes/{escapedSceneId}/resolve</c>).
    /// </summary>
    /// <param name="baseUrl">Origin/base URL; a single trailing slash is trimmed.</param>
    /// <param name="sceneId">Stable scene identifier.</param>
    public static string AbsoluteSceneResolveUrl(string baseUrl, string sceneId)
        => BuildAbsolute(baseUrl, SceneApiPrefix, sceneId, ResolveSuffix);

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
