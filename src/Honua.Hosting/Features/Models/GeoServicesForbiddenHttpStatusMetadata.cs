// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Models;

/// <summary>
/// Opts a mutating GeoServices endpoint's authorization refusal into HTTP 403 while
/// preserving its ArcGIS error envelope. Token challenges retain their existing behavior.
/// </summary>
internal sealed class GeoServicesForbiddenHttpStatusMetadata
{
    internal static GeoServicesForbiddenHttpStatusMetadata Instance { get; } = new();
}
