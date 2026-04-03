// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;

namespace Honua.Architecture.Tests;

/// <summary>
/// Shared helpers for architecture test classes.
/// </summary>
internal static class ArchitectureTestHelpers
{
    /// <summary>
    /// Returns all types from an assembly, gracefully handling <see cref="ReflectionTypeLoadException"/>
    /// which can occur when optional dependencies are not present.
    /// </summary>
    internal static IEnumerable<Type> GetTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }

    /// <summary>
    /// Resolves the repository root by walking upward until Honua.sln is found.
    /// </summary>
    internal static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("Unable to locate repository root.");
        }

        return directory.FullName;
    }
}
