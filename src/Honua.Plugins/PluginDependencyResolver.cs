// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins;

/// <summary>
/// Validates declared inter-plugin dependencies and minimum-server-version constraints at startup,
/// before any plugin code runs. Part of the manifest/security hardening (issue #1562): a missing
/// or version-incompatible dependency, or a server too old for a plugin, fails fast with a clear
/// message rather than surfacing as a runtime error later.
/// </summary>
internal static class PluginDependencyResolver
{
    /// <summary>
    /// Verifies that every plugin's declared dependencies are present at a compatible version and
    /// that the running server satisfies each plugin's minimum server version.
    /// </summary>
    /// <param name="registrations">All registered plugins.</param>
    /// <param name="serverVersion">The running server's semantic version.</param>
    /// <exception cref="InvalidOperationException">When a constraint is unsatisfied.</exception>
    public static void Validate(IReadOnlyList<PluginRegistration> registrations, Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(serverVersion);

        var byId = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            byId[registration.Id] = registration.Manifest;
        }

        // Not a candidate for .Select(r => r.Manifest): this loop validates and throws per
        // registration rather than projecting a new sequence, so a LINQ projection would not
        // improve clarity here.
        foreach (var manifest in (registrations).Select(registration => registration.Manifest))
        {

            if (manifest.MinimumServerVersion is { } minServer && serverVersion < minServer)
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' requires server version >= {minServer} but the server is {serverVersion}.");
            }

            foreach (var dependency in manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Id, out var depManifest))
                {
                    throw new InvalidOperationException(
                        $"Plugin '{manifest.Id}' depends on plugin '{dependency.Id}', which is not registered.");
                }

                if (dependency.MinimumVersion is { } required && depManifest.Version < required)
                {
                    throw new InvalidOperationException(
                        $"Plugin '{manifest.Id}' requires '{dependency.Id}' >= {required} "
                        + $"but version {depManifest.Version} is registered.");
                }
            }
        }
    }
}
