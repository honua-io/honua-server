// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed record PbfStreamEncodingOptions
{
    public bool ReturnGeometry { get; init; }
    public int? OutputSrid { get; init; }
    public bool ReturnZ { get; init; }
    public bool ReturnM { get; init; }
    public int? GeometryPrecision { get; init; }
    public double? MaxAllowableOffset { get; init; }
    public string[]? OutFields { get; init; }
    public QuantizationTransform? QuantizationTransform { get; init; }
}
