// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Config-bound experimental feature-flag switches for the capability registry
/// (#2339 / T2). Experimental capabilities (<see cref="CapabilityMaturity.Experimental"/>)
/// are off by default; these flags are the only lever that turns one on — either
/// globally or per-capability, per-environment — without re-forking (ADR-0058
/// feature-flag system). Bound from the <c>Capabilities:Experimental</c> section:
/// <list type="bullet">
///   <item><description>
///     <c>Capabilities:Experimental:Enabled</c> — the global master switch
///     (default <c>false</c>). When <c>true</c>, every experimental capability is on.
///   </description></item>
///   <item><description>
///     <c>Capabilities:Experimental:{descriptorId}:Enabled</c> — a per-capability
///     override (default <c>false</c>), for example
///     <c>Capabilities:Experimental:temporal.filtering:Enabled</c>.
///   </description></item>
/// </list>
/// The environment-variable form uses the double-underscore separator, for example
/// <c>Capabilities__Experimental__temporal.filtering__Enabled=true</c>.
/// </summary>
public sealed class CapabilityFlagOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Capabilities:Experimental";

    /// <summary>The config key of the global master switch within the section.</summary>
    public const string GlobalEnabledKey = "Enabled";

    /// <summary>The config key of a per-capability override within its subsection.</summary>
    public const string PerCapabilityEnabledKey = "Enabled";

    /// <summary>
    /// The global master switch (<c>Capabilities:Experimental:Enabled</c>). Defaults
    /// to <c>false</c>: with no configuration, no experimental capability is enabled.
    /// When <c>true</c>, it enables every experimental capability regardless of the
    /// per-capability overrides.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-capability overrides keyed by <see cref="CapabilityDescriptor.Id"/>. A
    /// missing entry is treated as disabled (default <c>false</c>).
    /// </summary>
    public IDictionary<string, ExperimentalCapabilityFlag> Capabilities { get; } =
        new Dictionary<string, ExperimentalCapabilityFlag>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the experimental capability with the given id is enabled: <c>true</c>
    /// when the global master switch is on, or when the capability has a per-capability
    /// override set to enabled. Non-experimental capabilities never consult this.
    /// </summary>
    /// <param name="capabilityId">The capability descriptor id to test.</param>
    public bool IsExperimentalEnabled(string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);

        if (Enabled)
        {
            return true;
        }

        return Capabilities.TryGetValue(capabilityId, out var flag) && flag.Enabled;
    }

    /// <summary>
    /// Binds the flag values from a <c>Capabilities:Experimental</c> configuration
    /// section. Handled explicitly (rather than via <c>Bind</c>) because the global
    /// <c>Enabled</c> scalar and the per-capability subsections are siblings under
    /// the same section, which the default binder cannot separate into a dictionary.
    /// </summary>
    /// <param name="options">The options instance to populate.</param>
    /// <param name="section">The <c>Capabilities:Experimental</c> section.</param>
    public static void Bind(CapabilityFlagOptions options, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(section);

        options.Enabled = section.GetValue(GlobalEnabledKey, false);

        foreach (var child in section.GetChildren())
        {
            // Skip the global master scalar; every other child is a per-capability
            // subsection keyed by the descriptor id.
            if (string.Equals(child.Key, GlobalEnabledKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            options.Capabilities[child.Key] = new ExperimentalCapabilityFlag
            {
                Enabled = child.GetValue(PerCapabilityEnabledKey, false),
            };
        }
    }
}

/// <summary>A single per-capability experimental override.</summary>
public sealed class ExperimentalCapabilityFlag
{
    /// <summary>Whether this experimental capability is enabled. Defaults to <c>false</c>.</summary>
    public bool Enabled { get; set; }
}
