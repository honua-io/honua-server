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
/// Higher-impact surfaces — running background work and contributing HTTP endpoints — do.
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
}
