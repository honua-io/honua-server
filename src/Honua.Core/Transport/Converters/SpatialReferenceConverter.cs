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

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for spatial reference systems between domain models and gRPC messages.
/// </summary>
public static class SpatialReferenceConverter
{
    /// <summary>
    /// Converts a domain SpatialReference to a gRPC SpatialReference message.
    /// </summary>
    /// <param name="domainSr">The domain spatial reference</param>
    /// <returns>gRPC spatial reference message</returns>
    public static Geospatial.V1.SpatialReference ToGrpc(Models.SpatialReference domainSr)
    {
        var grpcSr = new Geospatial.V1.SpatialReference();

        if (domainSr.WKID > 0)
        {
            grpcSr.Wkid = domainSr.WKID;
        }

        if (domainSr.LatestWKID.HasValue)
        {
            grpcSr.LatestWkid = domainSr.LatestWKID.Value;
        }

        if (!string.IsNullOrEmpty(domainSr.WKT))
        {
            grpcSr.Wkt = domainSr.WKT;
        }

        return grpcSr;
    }

    /// <summary>
    /// Converts a gRPC SpatialReference message to a domain SpatialReference.
    /// </summary>
    /// <param name="grpcSr">The gRPC spatial reference message</param>
    /// <returns>Domain spatial reference</returns>
    public static Models.SpatialReference FromGrpc(Geospatial.V1.SpatialReference grpcSr)
    {
        var domainSr = new Models.SpatialReference();

        if (grpcSr.Wkid > 0)
        {
            domainSr.WKID = grpcSr.Wkid;
        }

        if (grpcSr.LatestWkid > 0)
        {
            domainSr.LatestWKID = grpcSr.LatestWkid;
        }

        if (!string.IsNullOrEmpty(grpcSr.Wkt))
        {
            domainSr.WKT = grpcSr.Wkt;
        }

        return domainSr;
    }
}
