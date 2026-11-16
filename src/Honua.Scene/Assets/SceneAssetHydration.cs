// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Scene.Assets;

internal static class SceneAssetHydration
{
    private const string ScenesRoot = "scenes";

    internal const string ManifestObjectName = ".honua-manifest";

    internal const string MarkerFileName = ".honua-hydration";

    internal static string BuildPrefix(string sceneId) =>
        $"{ScenesRoot}/{sceneId}";

    internal static string BuildObjectKey(string prefix, string relativePath) =>
        $"{prefix}/{relativePath}";

    internal static string BuildToken(Guid datasetId, string prefix) =>
        string.Create(CultureInfo.InvariantCulture, $"{datasetId:N}|{prefix}");

    internal static bool IsMarkerCurrent(string localAssetRoot, string expectedToken)
    {
        // Safe: MarkerFileName is a hardcoded constant (".honua-hydration"), never
        // externally supplied, so this combine can never be rooted.
        var markerPath = Path.Join(localAssetRoot, MarkerFileName);
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

    internal static async Task WriteMarkerAsync(
        string directory,
        string token,
        CancellationToken cancellationToken)
    {
        // Safe: MarkerFileName is a hardcoded constant (".honua-hydration"), never
        // externally supplied, so this combine can never be rooted.
        var markerPath = Path.Join(directory, MarkerFileName);
        await File.WriteAllTextAsync(markerPath, token, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }
}
