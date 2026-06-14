// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Marks a type as the entry point of a Honua server plugin and carries its manifest
/// metadata (stable id, semantic version, optional description). Plugins are discovered
/// at compile time and registered in DI at startup — there is no runtime assembly loading,
/// which keeps the server Native-AOT compatible (issue #347, ADR-0018).
/// </summary>
/// <remarks>
/// The plugin SDK as a whole is an Enterprise-gated feature
/// (<c>plugin.sdk</c> entitlement); per-plugin edition gating is enforced by the host,
/// not by this attribute, to keep the public contract surface dependency-minimal.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAttribute"/> class.
    /// </summary>
    /// <param name="id">Stable, unique plugin identifier (e.g. <c>"utility-validation"</c>).</param>
    /// <param name="version">Semantic version of the plugin (e.g. <c>"1.0.0"</c>).</param>
    public PluginAttribute(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Id = id;
        Version = version;
    }

    /// <summary>
    /// Gets the stable, unique plugin identifier. Used to order plugin execution
    /// deterministically and to key per-plugin configuration and health reporting.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the semantic version of the plugin.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets or sets an optional human-readable description shown in health/diagnostics output.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the declarative capabilities this plugin requests. Capability-gated extension
    /// points (background services, custom endpoints) are only honored when the matching flag is
    /// present; otherwise registration fails fast at startup. Defaults to
    /// <see cref="PluginCapability.None"/>.
    /// </summary>
    public PluginCapability Capabilities { get; set; } = PluginCapability.None;

    /// <summary>
    /// Gets or sets the ids of other plugins this plugin depends on, as a comma- or
    /// semicolon-separated string (attribute arrays must be literal). The host verifies every
    /// declared dependency id is also registered at startup and fails fast otherwise. Each entry
    /// may carry an optional <c>"@&gt;=version"</c> suffix to constrain the dependency's version
    /// (e.g. <c>"geometry-rules@&gt;=1.2.0"</c>).
    /// </summary>
    public string? DependsOnCsv { get; set; }

    /// <summary>
    /// Gets or sets the minimum Honua server semantic version this plugin supports (e.g.
    /// <c>"1.4.0"</c>). When set, the host refuses to load the plugin on an older server.
    /// </summary>
    public string? MinimumServerVersion { get; set; }
}
