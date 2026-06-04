// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Routing telemetry source. The activity source name is registered with the
/// central tracer provider (Honua.ServiceDefaults) so per-solve spans export.
/// </summary>
internal static class RoutingTelemetry
{
    /// <summary>
    /// Activity source name for routing solves. Must match the entry added to the
    /// central <c>AddSource(...)</c> list in <c>Honua.ServiceDefaults</c>.
    /// </summary>
    public const string ActivitySourceName = "Honua.Routing";

    /// <summary>
    /// Shared activity source for routing solve spans.
    /// </summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);
}
