// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Plugins.Abstractions;

namespace Honua.Plugins;

public sealed record PluginDependency(string Id, Version? MinimumVersion);

public sealed record PluginManifest(
    string Id,
    Version Version,
    string? Description,
    PluginCapability Capabilities,
    ImmutableArray<PluginDependency> Dependencies,
    Version? MinimumServerVersion)
{
    public static PluginManifest FromAttribute(PluginAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var version = ParseVersion(attribute.Version, attribute.Id, "version");
        var minServer = attribute.MinimumServerVersion is { Length: > 0 } ms
            ? ParseVersion(ms, attribute.Id, "MinimumServerVersion")
            : null;

        return new PluginManifest(
            attribute.Id,
            version,
            attribute.Description,
            attribute.Capabilities,
            ParseDependencies(attribute.DependsOnCsv, attribute.Id),
            minServer);
    }

    private static ImmutableArray<PluginDependency> ParseDependencies(string? csv, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return ImmutableArray<PluginDependency>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<PluginDependency>();
        foreach (var raw in csv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var atIndex = raw.IndexOf("@>=", StringComparison.Ordinal);
            if (atIndex < 0)
            {
                builder.Add(new PluginDependency(raw, MinimumVersion: null));
                continue;
            }

            var id = raw[..atIndex].Trim();
            var versionText = raw[(atIndex + 3)..].Trim();
            if (id.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' declares a dependency with an empty id in '{raw}'.");
            }

            builder.Add(new PluginDependency(id, ParseVersion(versionText, pluginId, $"dependency '{id}' version")));
        }

        return builder.ToImmutable();
    }

    private static Version ParseVersion(string text, string pluginId, string field)
    {
        // Strip an optional semver pre-release/build suffix (e.g. "1.2.0-rc.1") to compare on the
        // numeric core; the SDK only enforces >= on the numeric components.
        var core = text;
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            core = core[..dash];
        }

        if (!Version.TryParse(core, out var version))
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' has an invalid {field}: '{text}'.");
        }

        return version;
    }

    public string VersionString() => Version.ToString();
}
