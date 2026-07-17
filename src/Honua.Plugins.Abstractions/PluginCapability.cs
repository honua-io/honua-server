// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Declarative capabilities a plugin must request on its <c>[Plugin]</c> manifest to use the more
/// powerful extension points. This is the in-process, AOT-compatible subset of the plugin security
/// model (issue #1562): rather than runtime sandboxing (which the Native-AOT profile precludes —
/// ADR-0018), the host enforces, at registration time, that a plugin has declared the capability
/// matching each extension point it implements. A plugin that implements a capability-gated
/// interface without declaring the capability fails fast at startup.
/// </summary>
/// <remarks>
/// The base validation extension points (<see cref="IFeatureValidator"/>,
/// <see cref="IFieldValidator"/>, <see cref="IEditHook"/>, <see cref="IComputedFieldProvider"/>)
/// are considered low-risk read/validate surfaces and do not require an explicit capability.
/// Higher-impact surfaces — running background work, contributing HTTP endpoints, contributing a
/// feature output format, and contributing a read-only data store — do.
/// </remarks>
[Flags]
public enum PluginCapability
{
    /// <summary>No elevated capabilities requested (validation/computed-field surfaces only).</summary>
    None = 0,

    /// <summary>
    /// Permits the plugin to run a long-running <see cref="IPluginBackgroundService"/> hosted by
    /// the server process.
    /// </summary>
    BackgroundExecution = 1 << 0,

    /// <summary>
    /// Permits the plugin to contribute additional HTTP endpoints via the custom-endpoint
    /// extension point. Contributed routes are still subject to the shared authorization,
    /// validation, and telemetry middleware.
    /// </summary>
    CustomEndpoints = 1 << 1,

    /// <summary>
    /// Permits the plugin to contribute a feature output format via the
    /// <see cref="IFeatureOutputFormat"/> extension point (issue #2856, ADR-0066). Contributed
    /// formats are consulted by the host's export/format-negotiation seam after the built-in
    /// formats and remain Enterprise-gated (<c>plugin.sdk</c>) plus honor the operator kill-switch.
    /// </summary>
    OutputFormats = 1 << 2,

    /// <summary>
    /// Permits the plugin to contribute a read-only vector data store by implementing the Core
    /// provider seam (<c>Honua.Core.Features.FeatureStore.Abstractions.IFeatureDataProvider</c>)
    /// (issue #2856, ADR-0066). The host registers the plugin as an additional feature-data
    /// provider so the existing provider registry/router bind layers to it by provider name —
    /// no runtime assembly loading, no router changes. Because the provider contract lives in
    /// <c>Honua.Core</c> rather than this minimal contract package, a data-store plugin
    /// necessarily references <c>Honua.Core</c> in addition to this SDK.
    /// </summary>
    DataStore = 1 << 3,
}
