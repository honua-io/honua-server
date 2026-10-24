// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.SensorThings;

/// <summary>
/// Configuration keys for the OGC SensorThings API (STA v1.1) protocol adapter.
/// </summary>
internal static class SensorThingsOptions
{
    /// <summary>Configuration section for SensorThings protocol behavior.</summary>
    public const string SectionName = "SensorThings";

    /// <summary>Canonical Preview opt-in that enables the SensorThings API route set.</summary>
    public const string ExperimentalFeatureFlagPath = "Capabilities:Experimental:serve.sensorthings:Enabled";

    /// <summary>
    /// Explicit opt-in path for accepting SensorThings write requests without authentication.
    /// </summary>
    public const string AllowAnonymousWritesDangerouslyPath = SectionName + ":AllowAnonymousWritesDangerously";
}
