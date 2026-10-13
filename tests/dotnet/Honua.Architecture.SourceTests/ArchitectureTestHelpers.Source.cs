// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Architecture.Tests;

internal static partial class ArchitectureTestHelpers
{
    /// <summary>
    /// Resolves the repository root by walking upward until Honua.sln is found.
    /// </summary>
    internal static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(CombinePath(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("Unable to locate repository root.");
        }

        return directory.FullName;
    }

    /// <summary>
    /// Combines path segments like <see cref="Path.Combine(string[])"/>, but throws instead of
    /// silently discarding the earlier segments if a non-first segment turns out to be rooted
    /// (absolute). Every call site in this project passes literal or catalog-derived relative
    /// segments, so this converts the silent-truncation risk CodeQL's <c>cs/path-combine</c>
    /// check flags into a loud test failure if that invariant is ever violated, instead of
    /// papering over the check with per-call-site suppression comments.
    /// </summary>
    internal static string CombinePath(params string[] segments)
    {
        for (var i = 1; i < segments.Length; i++)
        {
            if (Path.IsPathRooted(segments[i]))
            {
                throw new ArgumentException(
                    $"Path segment '{segments[i]}' at index {i} must be relative; Path.Combine would otherwise silently discard the preceding segments.",
                    nameof(segments));
            }
        }

        return Path.Join(segments);
    }

    /// <summary>
    /// Returns the bare project names (filename without extension) of every direct
    /// <c>&lt;ProjectReference&gt;</c> declared in the given csproj. Blank includes are
    /// skipped and Windows path separators are normalized so the result is stable
    /// across platforms.
    /// </summary>
    internal static IReadOnlyList<string> DirectProjectReferenceNames(string csprojPath)
        => DirectReferenceValues(csprojPath, "ProjectReference")
            .Select(value => Path.GetFileNameWithoutExtension(value.Replace('\\', '/'))!)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

    /// <summary>
    /// Returns the raw <c>Include</c> values of every direct
    /// <c>&lt;PackageReference&gt;</c> declared in the given csproj. Blank includes are
    /// skipped.
    /// </summary>
    internal static IReadOnlyList<string> DirectPackageReferenceNames(string csprojPath)
        => DirectReferenceValues(csprojPath, "PackageReference").ToList();

    private static IEnumerable<string> DirectReferenceValues(string csprojPath, string elementName)
        => XDocument.Load(csprojPath)
            .Descendants(elementName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
}
