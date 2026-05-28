// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Locates the canonical 3D Tiles fixture directory (<c>tests/fixtures/scenes/fixture-tileset</c>)
/// from the test runner's base directory by walking up to the repo root.
/// </summary>
internal static class SceneFixturePaths
{
    public const string FixtureSceneId = "fixture-tileset";
    public const string ProtectedSceneId = "protected-fixture-tileset";

    public static string ResolveFixtureRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tests", "fixtures", "scenes", "fixture-tileset");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            "Could not locate tests/fixtures/scenes/fixture-tileset from the test base directory.");
    }
}
