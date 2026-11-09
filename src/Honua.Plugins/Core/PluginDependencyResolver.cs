// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins;

internal static class PluginDependencyResolver
{
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
