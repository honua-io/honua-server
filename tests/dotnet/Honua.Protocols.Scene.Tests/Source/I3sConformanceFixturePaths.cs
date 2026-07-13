// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Locates the synthetic I3S 1.7 conformance fixture (#1813) under
/// <c>tests/fixtures/scene/i3s/</c> by walking up from the test runner's base
/// directory to the repo root.
/// </summary>
internal static class I3sConformanceFixturePaths
{
    /// <summary>
    /// Resolves the fixture's <c>source-tileset</c> directory — the hosted-scene
    /// asset root mounted by the conformance tests.
    /// </summary>
    public static string ResolveSourceTilesetRoot() => Resolve("source-tileset");

    /// <summary>Resolves the fixture's <c>expected</c> shape directory.</summary>
    public static string ResolveExpectedRoot() => Resolve("expected");

    private static string Resolve(string leaf)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            // `directory` is the only segment that can be absolute; every trailing
            // segment is a relative literal, so Path.Combine cannot drop it.
            var candidate = Path.Combine(directory, "tests", "fixtures", "scene", "i3s", leaf);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests/fixtures/scene/i3s/{leaf} from the test base directory.");
    }
}
