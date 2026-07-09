// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.SensorThings;

/// <summary>
/// Configuration keys for the OGC SensorThings API (STA v1.1) protocol adapter.
/// </summary>
internal static class SensorThingsOptions
{
    /// <summary>Configuration section for SensorThings protocol behavior.</summary>
    public const string SectionName = "SensorThings";

    /// <summary>Experimental feature flag path that enables the SensorThings API route set.</summary>
    public const string ExperimentalFeatureFlagPath = "Experimental:Features:SensorThings";

    /// <summary>
    /// Explicit opt-in path for accepting SensorThings write requests without authentication.
    /// </summary>
    public const string AllowAnonymousWritesDangerouslyPath = SectionName + ":AllowAnonymousWritesDangerously";
}
