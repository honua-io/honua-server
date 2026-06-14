// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Plugins.Abstractions;

namespace Honua.Plugins;

/// <summary>
/// A single declared dependency of a plugin: the id of another plugin and an optional minimum
/// semantic version the dependent requires. Parsed from the <c>DependsOnCsv</c> entries on the
/// <see cref="PluginAttribute"/> (e.g. <c>"geometry-rules@&gt;=1.2.0"</c>).
/// </summary>
/// <param name="Id">The depended-on plugin id.</param>
/// <param name="MinimumVersion">Optional minimum version constraint; <see langword="null"/> for any version.</param>
public sealed record PluginDependency(string Id, Version? MinimumVersion);

/// <summary>
/// The resolved manifest of a single plugin, captured at startup from its <c>[Plugin]</c>
/// attribute. Carries identity, the requested <see cref="PluginCapability"/> set, parsed
/// dependency constraints, and a minimum server version. Used to enforce the in-process security
/// model and dependency resolution before any plugin code runs.
/// </summary>
/// <param name="Id">Stable plugin identifier.</param>
/// <param name="Version">Plugin semantic version.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="Capabilities">Declared capability flags.</param>
/// <param name="Dependencies">Parsed dependency constraints.</param>
/// <param name="MinimumServerVersion">Optional minimum server version; <see langword="null"/> when unconstrained.</param>
public sealed record PluginManifest(
    string Id,
    Version Version,
    string? Description,
    PluginCapability Capabilities,
    ImmutableArray<PluginDependency> Dependencies,
    Version? MinimumServerVersion)
{
    /// <summary>
    /// Builds a <see cref="PluginManifest"/> from a plugin's <see cref="PluginAttribute"/>,
    /// validating that the version strings are well-formed semantic versions.
    /// </summary>
    /// <param name="attribute">The plugin's attribute.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="InvalidOperationException">When a version string is not a valid version.</exception>
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

    /// <summary>
    /// Formats the manifest version using the conventional dotted notation.
    /// </summary>
    /// <returns>The version string.</returns>
    public string VersionString() => Version.ToString();
}
