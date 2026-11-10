// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class FeatureServerExtrusionInfo
{
    public bool Enabled { get; init; } = true;

    public string HeightField { get; init; } = string.Empty;

    public string? BaseHeightField { get; init; }

    public string Unit { get; init; } = "meters";

    public double? DefaultHeight { get; init; }

    public string? MaterialHint { get; init; }
}
