// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit;

public static class RepositoryPaths
{
    public static string Resolve(params string[] segments)
    {
        var root = FindRepositoryRoot()
            ?? throw new FileNotFoundException(
                $"Unable to locate Honua.sln above {AppContext.BaseDirectory} when resolving repo-relative path.");

        if (segments.Length == 0)
        {
            return root;
        }

        // segments are always relative path fragments supplied by call sites (e.g.
        // "tests", "seed", "server.yaml"), never rooted, so root can't be dropped here.
        var parts = new string[segments.Length + 1];
        parts[0] = root;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.Join(parts);
    }

    private static string? FindRepositoryRoot()
    {
        // "Honua.sln" is a fixed literal marker filename, never rooted, so
        // directory.FullName can't be dropped here.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
