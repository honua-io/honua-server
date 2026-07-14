// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Runtime.Loader;
using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>Raised when the tool entrypoint cannot be loaded, resolved, or activated.</summary>
public sealed class EntrypointException(string message) : Exception(message);

/// <summary>
/// Loads a user tool's <see cref="IGeoprocessingTool"/> from a built assembly. The
/// entrypoint is <c>"Assembly::Namespace.Type"</c>: the assembly is loaded (with its
/// dependencies resolved against the build output directory), the type is resolved,
/// verified to implement <see cref="IGeoprocessingTool"/>, and activated via its public
/// parameterless constructor. The .NET analogue of the Python harness's
/// <c>loader.py</c> (which imports <c>module:func</c>).
/// </summary>
public sealed class EntrypointLoader
{
    private readonly Func<string, Assembly>? _assemblyLoader;

    /// <summary>Creates a loader.</summary>
    /// <param name="assemblyLoader">An injectable assembly loader keyed by assembly simple name (for tests).</param>
    public EntrypointLoader(Func<string, Assembly>? assemblyLoader = null)
    {
        _assemblyLoader = assemblyLoader;
    }

    /// <summary>
    /// Resolve and activate the <see cref="IGeoprocessingTool"/> named by
    /// <paramref name="entrypoint"/> from <paramref name="buildOutputDirectory"/>.
    /// </summary>
    /// <param name="buildOutputDirectory">The directory holding the built user assembly + its deps.</param>
    /// <param name="entrypoint">The <c>"Assembly::Namespace.Type"</c> entrypoint.</param>
    /// <returns>The activated tool instance.</returns>
    /// <exception cref="EntrypointException">On any load/resolve/activation failure.</exception>
    public IGeoprocessingTool Load(string buildOutputDirectory, string entrypoint)
    {
        var parts = entrypoint.Split("::", 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw new EntrypointException($"entrypoint '{entrypoint}' must be 'Assembly::Namespace.Type'.");
        }

        var assemblyName = parts[0];
        var typeName = parts[1];

        if (!IsSimpleAssemblyName(assemblyName))
        {
            throw new EntrypointException(
                $"entrypoint assembly '{assemblyName}' must be a simple name (no path separators, not rooted, no '..').");
        }

        Assembly assembly;
        try
        {
            assembly = _assemblyLoader is not null
                ? _assemblyLoader(assemblyName)
                : LoadFromDirectory(buildOutputDirectory, assemblyName);
        }
        catch (Exception ex) when (ex is not EntrypointException)
        {
            throw new EntrypointException($"failed to load assembly '{assemblyName}': {ex.Message}");
        }

        var type = assembly.GetType(typeName, throwOnError: false)
            ?? assembly.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, typeName, StringComparison.Ordinal))
            ?? throw new EntrypointException($"assembly '{assemblyName}' has no type '{typeName}'.");

        if (!typeof(IGeoprocessingTool).IsAssignableFrom(type))
        {
            throw new EntrypointException(
                $"type '{typeName}' does not implement {nameof(IGeoprocessingTool)}.");
        }

        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new EntrypointException(
                $"type '{typeName}' must be a concrete type with a public parameterless constructor.");
        }

        try
        {
            return (IGeoprocessingTool)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            // Intentional catch-all: constructor activation can throw any user-authored
            // exception type (including TargetInvocationException) and every failure must
            // surface as a well-defined EntrypointException, not crash the harness process.
            throw new EntrypointException($"failed to activate '{typeName}': {ex.Message}");
        }
    }

    private static Assembly LoadFromDirectory(string buildOutputDirectory, string assemblyName)
    {
        // False positive: every caller validates assemblyName via IsSimpleAssemblyName
        // (see Load, above) before reaching here, so it is never rooted/escaping.
        var path = Path.Combine(buildOutputDirectory, assemblyName + ".dll");
        if (!File.Exists(path))
        {
            throw new EntrypointException($"built assembly not found: {path}");
        }

        // A dedicated load context resolves the user assembly's dependencies from the
        // build output directory (where dotnet build copied them) without polluting the
        // harness's default context.
        var context = new UserAssemblyLoadContext(buildOutputDirectory);
        return context.LoadFromAssemblyPath(path);
    }

    /// <summary>
    /// True when <paramref name="name"/> is safe to combine with a directory to form a
    /// probe path: non-empty, no path separators, not rooted/absolute, and no <c>..</c>
    /// segment. Assembly names here originate from job-supplied entrypoints or from a
    /// user-built assembly's own dependency metadata, so a rooted/escaping name must
    /// never be trusted to silently resolve outside the build output directory.
    /// </summary>
    private static bool IsSimpleAssemblyName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        !name.Contains('/') &&
        !name.Contains('\\') &&
        !name.Contains("..", StringComparison.Ordinal) &&
        !Path.IsPathRooted(name);

    private sealed class UserAssemblyLoadContext(string probeDirectory) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(probeDirectory);
        private readonly string _probeDirectory = probeDirectory;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null)
            {
                return LoadFromAssemblyPath(resolved);
            }

            // Fall back to a sibling DLL in the build output (deps copied next to the tool).
            if (assemblyName.Name is { } name && IsSimpleAssemblyName(name))
            {
                // False positive: the IsSimpleAssemblyName guard immediately above rejects
                // any rooted/escaping name before it reaches this combine.
                var candidate = Path.Combine(_probeDirectory, name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }

            // Returning null defers to the default context so the SHARED contract
            // assembly (Honua.CustomCode.Sdk) loaded by the harness is reused — the tool
            // and harness must agree on the same IGeoprocessingTool type identity.
            return null;
        }
    }
}
