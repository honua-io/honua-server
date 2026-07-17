// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Host-facing lookup of plugin-contributed <see cref="IFeatureOutputFormat"/>s (issue #2856,
/// ADR-0066). A single implementation is resolved from DI; when the plugin SDK is unlicensed, the
/// operator kill-switch is off, or no output-format plugins are registered, the host registers
/// <see cref="NoOpFeatureOutputFormatRegistry"/> so the export/negotiation seam has zero overhead
/// and can depend on this abstraction unconditionally.
/// </summary>
/// <remarks>
/// This is the only output-format type a format-negotiation seam (for example the admin export
/// endpoint) needs to consume — it depends solely on this contract package and never on plugin
/// internals. Consult it only for formats not already served by a built-in writer.
/// </remarks>
public interface IFeatureOutputFormatRegistry
{
    /// <summary>
    /// Gets whether any plugin output formats are active (licensed, enabled, and registered). Hosts
    /// can skip plugin-format resolution entirely when this is <see langword="false"/>.
    /// </summary>
    bool HasFormats { get; }

    /// <summary>
    /// Gets the advertised plugin formats, for capabilities/discovery listings. Empty when
    /// <see cref="HasFormats"/> is <see langword="false"/>.
    /// </summary>
    IReadOnlyCollection<PluginOutputFormatDescriptor> AdvertisedFormats { get; }

    /// <summary>
    /// Resolves a plugin output format by its wire token, honoring the Enterprise entitlement and
    /// operator kill-switch.
    /// </summary>
    /// <param name="formatId">The requested format token, matched case-insensitively.</param>
    /// <param name="format">The resolved writer when found and active; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when an active plugin format matches.</returns>
    bool TryGetFormat(string formatId, out IFeatureOutputFormat? format);
}
