// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.SensorThings;

internal static class SensorThingsOptions
{
    public const string SectionName = "SensorThings";

    public const string ExperimentalFeatureFlagPath = "Capabilities:Experimental:serve.sensorthings:Enabled";

    public const string AllowAnonymousWritesDangerouslyPath = SectionName + ":AllowAnonymousWritesDangerously";
}
