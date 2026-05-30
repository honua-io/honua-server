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
    /// Loads every assembly that may carry integration tests with endpoint /
    /// operation coverage attributes: the monolithic <c>Honua.Server.Tests</c>
    /// plus each extracted per-protocol test project
    /// (<c>Honua.Protocols.&lt;X&gt;.Tests</c>). They are discovered by globbing
    /// the test output directory, so a newly extracted protocol test project is
    /// picked up automatically once Architecture.Tests references it (which puts
    /// its assembly in the output directory).
    /// </summary>
    /// <remarks>
    /// Coverage scans must union these assemblies: as protocols are physically
    /// split out of Honua.Server, their integration tests move into the
    /// matching <c>Honua.Protocols.*.Tests</c> assembly. Anchoring on a single
    /// <c>Honua.Server.Tests</c> type would silently lose coverage for every
    /// extracted protocol.
    /// </remarks>
    internal static IReadOnlyList<Assembly> IntegrationTestAssemblies()
    {
        var baseDir = AppContext.BaseDirectory;
        // Honua.Ai.Tests carries the MCP protocol-surface tests (MCP lives in
        // Honua.Ai, not a standalone Honua.Protocols.Mcp), so it holds endpoint /
        // operation coverage that the scans must see alongside the Server.Tests
        // and per-protocol Honua.Protocols.*.Tests assemblies.
        var patterns = new[] { "Honua.Server.Tests.dll", "Honua.Protocols.*.Tests.dll", "Honua.Ai.Tests.dll" };

        var assemblies = new List<Assembly>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(baseDir, pattern))
            {
                if (!seen.Add(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    assemblies.Add(Assembly.LoadFrom(path));
                }
                catch (BadImageFormatException)
                {
                    // Native / mixed-mode sidecar that happens to match the glob — skip.
                }
            }
        }

        return assemblies;
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
