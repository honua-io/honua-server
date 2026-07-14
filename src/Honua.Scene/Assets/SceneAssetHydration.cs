// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Scene.Assets;

/// <summary>
/// Shared object-store layout and local-cache marker mechanics for the scene
/// asset read-through cache (#2459, ADR-0060). Both the publish path (which
/// uploads the promoted tileset tree and stamps the local cache) and the serving
/// path (<see cref="SceneAssetHydrator"/>, which hydrates a fresh node) rely on
/// the identical prefix, manifest, and marker conventions so the two sides agree.
/// </summary>
internal static class SceneAssetHydration
{
    /// <summary>
    /// Root object-store prefix under which every scene's replicated tree lives.
    /// </summary>
    private const string ScenesRoot = "scenes";

    /// <summary>
    /// Object name (relative to the scene prefix) of the newline-delimited manifest
    /// of relative asset paths. Downloaded first by a hydrating node so it never
    /// depends on list-API pagination or ordering across storage providers.
    /// </summary>
    internal const string ManifestObjectName = ".honua-manifest";

    /// <summary>
    /// File written into a materialized local asset root to stamp which storage
    /// version it was hydrated from. A mismatch (republish, changed prefix)
    /// re-hydrates; a match is a fast no-op on the serving hot path.
    /// </summary>
    internal const string MarkerFileName = ".honua-hydration";

    /// <summary>
    /// Builds the stable object-store prefix for a scene's replicated tree.
    /// </summary>
    internal static string BuildPrefix(string sceneId) =>
        $"{ScenesRoot}/{sceneId}";

    /// <summary>
    /// Builds the object key for an asset at <paramref name="relativePath"/> under
    /// the scene <paramref name="prefix"/>. Relative paths always use forward
    /// slashes so keys are portable across storage providers.
    /// </summary>
    internal static string BuildObjectKey(string prefix, string relativePath) =>
        $"{prefix}/{relativePath}";

    /// <summary>
    /// Builds the content-version token stamped into the local marker. The token
    /// binds the local cache to a specific registration (<paramref name="datasetId"/>)
    /// and storage prefix: a republish mints a new dataset id, so the token differs
    /// and the stale cache re-hydrates.
    /// </summary>
    internal static string BuildToken(Guid datasetId, string prefix) =>
        string.Create(CultureInfo.InvariantCulture, $"{datasetId:N}|{prefix}");

    /// <summary>
    /// Returns <c>true</c> when <paramref name="localAssetRoot"/> already holds a
    /// marker matching <paramref name="expectedToken"/>, meaning the local cache is
    /// current and hydration can be skipped.
    /// </summary>
    internal static bool IsMarkerCurrent(string localAssetRoot, string expectedToken)
    {
        // Safe: MarkerFileName is a hardcoded constant (".honua-hydration"), never
        // externally supplied, so this combine can never be rooted.
        var markerPath = Path.Combine(localAssetRoot, MarkerFileName);
        try
        {
            if (!File.Exists(markerPath))
            {
                return false;
            }
            var actual = File.ReadAllText(markerPath, Encoding.UTF8).Trim();
            return string.Equals(actual, expectedToken, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the local-cache marker for <paramref name="token"/> into
    /// <paramref name="directory"/>. Called by the publish path (into the staging
    /// directory, so the stamp travels with the atomic promote) and by the hydrator
    /// (into the freshly downloaded temp directory before install).
    /// </summary>
    internal static async Task WriteMarkerAsync(
        string directory,
        string token,
        CancellationToken cancellationToken)
    {
        // Safe: MarkerFileName is a hardcoded constant (".honua-hydration"), never
        // externally supplied, so this combine can never be rooted.
        var markerPath = Path.Combine(directory, MarkerFileName);
        await File.WriteAllTextAsync(markerPath, token, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }
}
