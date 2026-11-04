// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Geocoding.Features.Geocoding.Services;

internal static class GeocodingTelemetry
{
    // This activity source name must match the service-defaults allow-list or its spans are dropped.
    public const string ActivitySourceName = "Honua.Geocoding";

    public static readonly ActivitySource Source = new(ActivitySourceName);
}
