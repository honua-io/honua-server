// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

internal static class I3sConformanceFixturePaths
{
    public static string ResolveSourceTilesetRoot() => Resolve("source-tileset");

    public static string ResolveExpectedRoot() => Resolve("expected");

    private static string Resolve(string leaf)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            // `directory` is the only segment that can be absolute; every trailing
            // segment is a relative literal, so Path.Combine cannot drop it.
            var candidate = Path.Join(directory, "tests", "fixtures", "scene", "i3s", leaf);
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
