// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class ExtentInfo
{
    public required double Xmin { get; init; }

    public required double Ymin { get; init; }

    public required double Xmax { get; init; }

    public required double Ymax { get; init; }

    public required SpatialReferenceInfo SpatialReference { get; init; }

    public static ExtentInfo FromBoundingBox(BoundingBox boundingBox, SpatialReference? spatialReference = null)
        => new()
        {
            Xmin = boundingBox.MinX,
            Ymin = boundingBox.MinY,
            Xmax = boundingBox.MaxX,
            Ymax = boundingBox.MaxY,
            SpatialReference = (spatialReference ?? (boundingBox.SpatialReferenceId.HasValue
                ? Honua.Core.Features.Shared.Models.SpatialReference.Create(boundingBox.SpatialReferenceId.Value)
                : Honua.Core.Features.Shared.Models.SpatialReference.WGS84)).ToSpatialReferenceInfo()
        };

    public BoundingBox ToBoundingBox()
        => BoundingBox.Create(Xmin, Ymin, Xmax, Ymax, SpatialReference.Wkid);
}
