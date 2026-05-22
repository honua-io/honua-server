// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit;

/// <summary>
/// Resolves repository-relative file paths from a test's runtime working directory by
/// walking upward to the <c>Honua.sln</c> marker. New tests that need to load seed
/// SQL, fixtures, or other repo-relative assets should use this helper instead of
/// duplicating the ancestor walk inline.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>
    /// Returns the absolute path of <paramref name="segments"/> resolved against the
    /// repository root (the directory containing <c>Honua.sln</c>).
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the repository root cannot be located from <see cref="AppContext.BaseDirectory"/>.
    /// </exception>
    public static string Resolve(params string[] segments)
    {
        var root = FindRepositoryRoot()
            ?? throw new FileNotFoundException(
                $"Unable to locate Honua.sln above {AppContext.BaseDirectory} when resolving repo-relative path.");

        if (segments.Length == 0)
        {
            return root;
        }

        var parts = new string[segments.Length + 1];
        parts[0] = root;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.Combine(parts);
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
