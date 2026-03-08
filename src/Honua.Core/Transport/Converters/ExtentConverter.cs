// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for extents (bounding boxes) between domain models and gRPC messages.
/// </summary>
public static class ExtentConverter
{
    /// <summary>
    /// Converts a domain FeatureExtent to a gRPC Extent message.
    /// </summary>
    /// <param name="domainExtent">The domain extent</param>
    /// <returns>gRPC extent message</returns>
    public static Geospatial.V1.Extent ToGrpc(FeatureExtent domainExtent)
    {
        var grpcExtent = new Geospatial.V1.Extent
        {
            Xmin = domainExtent.MinX,
            Ymin = domainExtent.MinY,
            Xmax = domainExtent.MaxX,
            Ymax = domainExtent.MaxY
        };

        var spatialRef = new Models.SpatialReference { WKID = domainExtent.SpatialReference };
        grpcExtent.SpatialReference = SpatialReferenceConverter.ToGrpc(spatialRef);

        return grpcExtent;
    }

    /// <summary>
    /// Converts a gRPC Extent message to a domain FeatureExtent.
    /// </summary>
    /// <param name="grpcExtent">The gRPC extent message</param>
    /// <returns>Domain extent</returns>
    public static FeatureExtent FromGrpc(Geospatial.V1.Extent grpcExtent)
    {
        int srid = 4326; // Default to WGS84
        if (grpcExtent.SpatialReference != null)
        {
            var spatialRef = SpatialReferenceConverter.FromGrpc(grpcExtent.SpatialReference);
            srid = spatialRef.WKID;
        }

        return FeatureExtent.Create(
            minX: grpcExtent.Xmin,
            minY: grpcExtent.Ymin,
            maxX: grpcExtent.Xmax,
            maxY: grpcExtent.Ymax,
            spatialReference: srid
        );
    }
}
