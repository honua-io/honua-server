// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoServicesSpatialReference
{
    public int? Wkid { get; init; }

    public int? LatestWkid { get; init; }

    public int? VcsWkid { get; init; }

    public int? LatestVcsWkid { get; init; }

    public string? Wkt { get; init; }
}
