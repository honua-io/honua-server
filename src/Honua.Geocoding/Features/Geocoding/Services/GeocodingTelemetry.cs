// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Geocoding.Features.Geocoding.Services;

/// <summary>
/// Geocoding telemetry source. The activity source name is registered with the
/// central tracer provider (Honua.ServiceDefaults) so per-geocode spans export.
/// </summary>
internal static class GeocodingTelemetry
{
    /// <summary>
    /// Activity source name for geocoding operations. Must match the entry added to
    /// the central <c>AddSource(...)</c> list in <c>Honua.ServiceDefaults</c>.
    /// </summary>
    public const string ActivitySourceName = "Honua.Geocoding";

    /// <summary>
    /// Shared activity source for forward/reverse geocode spans.
    /// </summary>
    public static readonly ActivitySource Source = new(ActivitySourceName);
}
