// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Walks up from the test runner's base directory to locate a named scene
/// fixture directory under <c>tests/fixtures/scenes/</c>. Shared by the
/// per-fixture path helpers so the repo-root discovery logic lives in one place.
/// </summary>
internal static class SceneFixtureRoots
{
    public static string Resolve(string fixtureName)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tests", "fixtures", "scenes", fixtureName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests/fixtures/scenes/{fixtureName} from the test base directory.");
    }
}
