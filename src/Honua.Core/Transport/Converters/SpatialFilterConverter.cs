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

using Geospatial.V1;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Transport.Converters;

/// <summary>
/// Converter for spatial filters between domain models and gRPC messages.
/// </summary>
public static class SpatialFilterConverter
{
    /// <summary>
    /// Converts a domain SpatialFilter to a gRPC SpatialFilter message.
    /// </summary>
    /// <param name="domainFilter">The domain spatial filter</param>
    /// <returns>gRPC spatial filter message</returns>
    public static Geospatial.V1.SpatialFilter ToGrpc(Features.FeatureStore.Domain.SpatialFilter domainFilter)
    {
        // Convert WKB byte array to NTS Geometry, then to gRPC Geometry
        var ntsGeometry = GeometryConverter.FromWkb(domainFilter.Geometry);

        var grpcFilter = new Geospatial.V1.SpatialFilter
        {
            Geometry = GeometryConverter.ToGrpc(ntsGeometry),
            SpatialRelationship = ConvertSpatialRelationship(domainFilter.SpatialRelationship)
        };

        if (domainFilter.Srid.HasValue)
        {
            var spatialRef = new Models.SpatialReference { WKID = domainFilter.Srid.Value };
            grpcFilter.SpatialReference = SpatialReferenceConverter.ToGrpc(spatialRef);
        }

        if (domainFilter.Distance.HasValue)
        {
            grpcFilter.Distance = domainFilter.Distance.Value;
        }

        grpcFilter.DistanceUnit = ConvertDistanceUnit(domainFilter.DistanceUnit);

        return grpcFilter;
    }

    /// <summary>
    /// Converts a gRPC SpatialFilter message to a domain SpatialFilter.
    /// </summary>
    /// <param name="grpcFilter">The gRPC spatial filter message</param>
    /// <returns>Domain spatial filter</returns>
    public static Features.FeatureStore.Domain.SpatialFilter FromGrpc(Geospatial.V1.SpatialFilter grpcFilter)
    {
        // Convert gRPC Geometry to NTS Geometry, then to WKB byte array
        var ntsGeometry = GeometryConverter.FromGrpc(grpcFilter.Geometry);

        return Features.FeatureStore.Domain.SpatialFilter.Create(
            geometry: GeometryConverter.ToWkb(ntsGeometry),
            spatialRelationship: ConvertSpatialRelationship(grpcFilter.SpatialRelationship),
            srid: grpcFilter.SpatialReference?.Wkid
        ) with
        {
            Distance = grpcFilter.Distance > 0 ? grpcFilter.Distance : null,
            DistanceUnit = ConvertDistanceUnit(grpcFilter.DistanceUnit)
        };
    }

    private static Geospatial.V1.SpatialRelationship ConvertSpatialRelationship(Features.FeatureStore.Domain.SpatialRelationship domainRelationship)
    {
        return domainRelationship switch
        {
            Features.FeatureStore.Domain.SpatialRelationship.Intersects => Geospatial.V1.SpatialRelationship.Intersects,
            Features.FeatureStore.Domain.SpatialRelationship.Contains => Geospatial.V1.SpatialRelationship.Contains,
            Features.FeatureStore.Domain.SpatialRelationship.Within => Geospatial.V1.SpatialRelationship.Within,
            Features.FeatureStore.Domain.SpatialRelationship.Crosses => Geospatial.V1.SpatialRelationship.Crosses,
            Features.FeatureStore.Domain.SpatialRelationship.Touches => Geospatial.V1.SpatialRelationship.Touches,
            Features.FeatureStore.Domain.SpatialRelationship.Overlaps => Geospatial.V1.SpatialRelationship.Overlaps,
            Features.FeatureStore.Domain.SpatialRelationship.Disjoint => Geospatial.V1.SpatialRelationship.Disjoint,
            Features.FeatureStore.Domain.SpatialRelationship.Equals => Geospatial.V1.SpatialRelationship.Equals,
            Features.FeatureStore.Domain.SpatialRelationship.EnvelopeIntersects => Geospatial.V1.SpatialRelationship.EnvelopeIntersects,
            Features.FeatureStore.Domain.SpatialRelationship.WithinDistance => Geospatial.V1.SpatialRelationship.WithinDistance,
            Features.FeatureStore.Domain.SpatialRelationship.BeyondDistance => Geospatial.V1.SpatialRelationship.BeyondDistance,
            Features.FeatureStore.Domain.SpatialRelationship.NearestNeighbor => Geospatial.V1.SpatialRelationship.NearestNeighbor,
            _ => Geospatial.V1.SpatialRelationship.Unspecified
        };
    }

    private static Features.FeatureStore.Domain.SpatialRelationship ConvertSpatialRelationship(Geospatial.V1.SpatialRelationship grpcRelationship)
    {
        return grpcRelationship switch
        {
            Geospatial.V1.SpatialRelationship.Intersects => Features.FeatureStore.Domain.SpatialRelationship.Intersects,
            Geospatial.V1.SpatialRelationship.Contains => Features.FeatureStore.Domain.SpatialRelationship.Contains,
            Geospatial.V1.SpatialRelationship.Within => Features.FeatureStore.Domain.SpatialRelationship.Within,
            Geospatial.V1.SpatialRelationship.Crosses => Features.FeatureStore.Domain.SpatialRelationship.Crosses,
            Geospatial.V1.SpatialRelationship.Touches => Features.FeatureStore.Domain.SpatialRelationship.Touches,
            Geospatial.V1.SpatialRelationship.Overlaps => Features.FeatureStore.Domain.SpatialRelationship.Overlaps,
            Geospatial.V1.SpatialRelationship.Disjoint => Features.FeatureStore.Domain.SpatialRelationship.Disjoint,
            Geospatial.V1.SpatialRelationship.Equals => Features.FeatureStore.Domain.SpatialRelationship.Equals,
            Geospatial.V1.SpatialRelationship.EnvelopeIntersects => Features.FeatureStore.Domain.SpatialRelationship.EnvelopeIntersects,
            Geospatial.V1.SpatialRelationship.WithinDistance => Features.FeatureStore.Domain.SpatialRelationship.WithinDistance,
            Geospatial.V1.SpatialRelationship.BeyondDistance => Features.FeatureStore.Domain.SpatialRelationship.BeyondDistance,
            Geospatial.V1.SpatialRelationship.NearestNeighbor => Features.FeatureStore.Domain.SpatialRelationship.NearestNeighbor,
            _ => Features.FeatureStore.Domain.SpatialRelationship.Intersects // Default fallback
        };
    }

    private static Geospatial.V1.DistanceUnit ConvertDistanceUnit(Features.FeatureStore.Domain.DistanceUnit domainUnit)
    {
        return domainUnit switch
        {
            Features.FeatureStore.Domain.DistanceUnit.Meters => Geospatial.V1.DistanceUnit.Meters,
            Features.FeatureStore.Domain.DistanceUnit.Feet => Geospatial.V1.DistanceUnit.Feet,
            Features.FeatureStore.Domain.DistanceUnit.Kilometers => Geospatial.V1.DistanceUnit.Kilometers,
            Features.FeatureStore.Domain.DistanceUnit.Miles => Geospatial.V1.DistanceUnit.Miles,
            _ => Geospatial.V1.DistanceUnit.Unspecified
        };
    }

    private static Features.FeatureStore.Domain.DistanceUnit ConvertDistanceUnit(Geospatial.V1.DistanceUnit grpcUnit)
    {
        return grpcUnit switch
        {
            Geospatial.V1.DistanceUnit.Meters => Features.FeatureStore.Domain.DistanceUnit.Meters,
            Geospatial.V1.DistanceUnit.Feet => Features.FeatureStore.Domain.DistanceUnit.Feet,
            Geospatial.V1.DistanceUnit.Kilometers => Features.FeatureStore.Domain.DistanceUnit.Kilometers,
            Geospatial.V1.DistanceUnit.Miles => Features.FeatureStore.Domain.DistanceUnit.Miles,
            _ => Features.FeatureStore.Domain.DistanceUnit.Meters // Default fallback
        };
    }
}