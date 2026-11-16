// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;

namespace Honua.Architecture.Tests;

internal static partial class ArchitectureTestHelpers
{
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

    internal static IReadOnlyList<string> DirectProjectReferenceNames(string csprojPath)
        => DirectReferenceValues(csprojPath, "ProjectReference")
            .Select(value => Path.GetFileNameWithoutExtension(value.Replace('\\', '/'))!)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

    internal static IReadOnlyList<string> DirectPackageReferenceNames(string csprojPath)
        => DirectReferenceValues(csprojPath, "PackageReference").ToList();

    private static IEnumerable<string> DirectReferenceValues(string csprojPath, string elementName)
        => XDocument.Load(csprojPath)
            .Descendants(elementName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
}
