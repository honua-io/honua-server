// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Protocols.GeoServices.NAServer.Models;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Protocols.GeoServices.NAServer;

/// <summary>
/// Maps canonical <see cref="Honua.Routing"/> solve results into NAServer Esri JSON
/// response DTOs. The canonical results carry geometry as GeoJSON strings; this helper
/// performs the wire-format conversion to Esri <c>paths</c>/<c>rings</c> (a
/// protocol-local concern). No routing logic lives here.
/// </summary>
internal static class NAServerResultMapping
{
    /// <summary>
    /// Maps a route solve result into the NAServer route response. <paramref name="includeDirections"/>
    /// controls whether turn-by-turn directions are emitted.
    /// </summary>
    public static NAServerRouteSolveResponse MapRoute(
        RouteSolveResult result,
        int outSrid,
        bool includeRoutes,
        bool includeDirections)
    {
        ArgumentNullException.ThrowIfNull(result);

        var spatialReference = BuildSpatialReference(outSrid);
        var messages = new List<NAServerMessage>();

        NAServerRouteFeatureSet routes;
        if (!result.Solved)
        {
            messages.Add(new NAServerMessage
            {
                Type = 50,
                Description = "No route could be solved between the supplied stops.",
            });
            routes = new NAServerRouteFeatureSet { GeometryType = "esriGeometryPolyline", SpatialReference = spatialReference };
        }
        else if (!includeRoutes)
        {
            routes = new NAServerRouteFeatureSet { GeometryType = "esriGeometryPolyline", SpatialReference = spatialReference };
        }
        else
        {
            var paths = GeoJsonToEsri.ToPaths(result.RouteGeometryGeoJson);
            routes = new NAServerRouteFeatureSet
            {
                GeometryType = "esriGeometryPolyline",
                SpatialReference = spatialReference,
                Features =
                [
                    new NAServerRouteFeature
                    {
                        Geometry = new NAServerPolylineGeometry
                        {
                            Paths = paths,
                            SpatialReference = spatialReference,
                        },
                        Attributes = new NAServerRouteAttributes
                        {
                            Name = "Route 1",
                            TotalLength = result.TotalLengthMeters,
                            TotalTravelTime = result.TotalTimeMinutes,
                        },
                    },
                ],
            };
        }

        var directions = includeDirections && result.Solved
            ? MapDirections(result.Directions)
            : [];

        return new NAServerRouteSolveResponse
        {
            Routes = routes,
            Directions = directions,
            Messages = messages.Count > 0 ? [.. messages] : null,
        };
    }

    /// <summary>
    /// Maps a service-area solve result into the NAServer service-area response.
    /// </summary>
    public static NAServerServiceAreaResponse MapServiceArea(ServiceAreaSolveResult result, int outSrid)
    {
        ArgumentNullException.ThrowIfNull(result);

        var spatialReference = BuildSpatialReference(outSrid);
        var features = new List<NAServerSaPolygonFeature>(result.Polygons.Count);
        var objectId = 1;

        foreach (var polygon in result.Polygons)
        {
            var rings = GeoJsonToEsri.ToRings(polygon.GeometryGeoJson);
            features.Add(new NAServerSaPolygonFeature
            {
                Geometry = new NAServerPolygonGeometry
                {
                    Rings = rings,
                    SpatialReference = spatialReference,
                },
                Attributes = new NAServerSaPolygonAttributes
                {
                    ObjectId = objectId,
                    FacilityId = polygon.FacilityId,
                    Name = $"Facility {polygon.FacilityId} : {FormatBreak(polygon.FromBreak)} - {FormatBreak(polygon.ToBreak)}",
                    FromBreak = polygon.FromBreak,
                    ToBreak = polygon.ToBreak,
                },
            });
            objectId++;
        }

        NAServerMessage[]? messages = null;
        if (features.Count == 0)
        {
            messages =
            [
                new NAServerMessage
                {
                    Type = 50,
                    Description = "No service-area polygons could be generated for the supplied facilities and breaks.",
                },
            ];
        }

        return new NAServerServiceAreaResponse
        {
            SaPolygons = new NAServerSaPolygonsFeatureSet
            {
                GeometryType = "esriGeometryPolygon",
                SpatialReference = spatialReference,
                Features = [.. features],
            },
            Messages = messages,
        };
    }

    private static NAServerDirection[] MapDirections(IReadOnlyList<RouteDirectionStep> steps)
    {
        if (steps.Count == 0)
        {
            return [];
        }

        var features = new NAServerDirectionFeature[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            features[i] = new NAServerDirectionFeature
            {
                Attributes = new NAServerDirectionAttributes
                {
                    Text = step.Text,
                    Length = step.Length,
                    Time = step.Time,
                    ManeuverType = step.ManeuverType,
                },
            };
        }

        return
        [
            new NAServerDirection { Features = features },
        ];
    }

    private static NAServerSpatialReference BuildSpatialReference(int srid)
        => new() { Wkid = srid, LatestWkid = srid };

    private static string FormatBreak(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Minimal GeoJSON-coordinates → Esri paths/rings converter. The routing
    /// providers emit simple GeoJSON <c>LineString</c> / <c>Polygon</c> /
    /// <c>MultiLineString</c> / <c>MultiPolygon</c> geometries, so a coordinate-walk
    /// is sufficient and avoids a NetTopologySuite round-trip.
    /// </summary>
    private static class GeoJsonToEsri
    {
        public static double[][][] ToPaths(string? geoJson)
        {
            if (string.IsNullOrWhiteSpace(geoJson))
            {
                return [];
            }

            using var doc = ParseOrNull(geoJson);
            if (doc is null)
            {
                return [];
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("coordinates", out var coordinates))
            {
                return [];
            }

            var type = typeElement.GetString();
            return type switch
            {
                "LineString" => [ReadLine(coordinates)],
                "MultiLineString" => ReadLines(coordinates),
                _ => [],
            };
        }

        public static double[][][] ToRings(string? geoJson)
        {
            if (string.IsNullOrWhiteSpace(geoJson))
            {
                return [];
            }

            using var doc = ParseOrNull(geoJson);
            if (doc is null)
            {
                return [];
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("coordinates", out var coordinates))
            {
                return [];
            }

            var type = typeElement.GetString();
            return type switch
            {
                // Polygon coordinates: [ outerRing, hole, hole, ... ]
                "Polygon" => ReadPolygonRings(coordinates),
                // MultiPolygon coordinates: [ polygon, polygon, ... ] → flatten rings.
                "MultiPolygon" => ReadMultiPolygonRings(coordinates),
                _ => [],
            };
        }

        private static JsonDocument? ParseOrNull(string json)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static double[][][] ReadLines(JsonElement arrayOfLines)
        {
            if (arrayOfLines.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var lines = new List<double[][]>(arrayOfLines.GetArrayLength());
            foreach (var line in arrayOfLines.EnumerateArray())
            {
                lines.Add(ReadLine(line));
            }

            return [.. lines];
        }

        private static double[][][] ReadMultiPolygonRings(JsonElement multiPolygon)
        {
            if (multiPolygon.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var rings = new List<double[][]>();
            foreach (var polygon in multiPolygon.EnumerateArray())
            {
                if (polygon.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                rings.AddRange(ReadPolygonRings(polygon));
            }

            return [.. rings];
        }

        /// <summary>
        /// Reads a GeoJSON polygon ring array ([ outerRing, hole, ... ]) and
        /// normalizes winding for Esri: outer ring clockwise, holes
        /// counter-clockwise. GeoJSON (RFC 7946) mandates the opposite convention
        /// (CCW outer, CW holes), so each ring is reversed when its signed area does
        /// not match the Esri orientation.
        /// </summary>
        private static double[][][] ReadPolygonRings(JsonElement polygon)
        {
            if (polygon.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var rings = new List<double[][]>(polygon.GetArrayLength());
            var ringIndex = 0;
            foreach (var ring in polygon.EnumerateArray())
            {
                // First ring is the exterior (clockwise for Esri); the rest are
                // holes (counter-clockwise for Esri).
                var wantClockwise = ringIndex == 0;
                rings.Add(NormalizeRingWinding(ReadLine(ring), wantClockwise));
                ringIndex++;
            }

            return [.. rings];
        }

        /// <summary>
        /// Ensures a ring's vertex order matches the requested winding (clockwise
        /// when <paramref name="wantClockwise"/> is true) using a signed-area test.
        /// In the standard math/GeoJSON convention a positive signed area is
        /// counter-clockwise, so a clockwise target wants a negative area.
        /// </summary>
        private static double[][] NormalizeRingWinding(double[][] ring, bool wantClockwise)
        {
            if (ring.Length < 4)
            {
                // Fewer than 4 positions cannot form a closed, oriented ring; leave
                // it untouched rather than guessing an orientation.
                return ring;
            }

            var signedArea = SignedArea(ring);
            var isClockwise = signedArea < 0;
            if (isClockwise == wantClockwise)
            {
                return ring;
            }

            Array.Reverse(ring);
            return ring;
        }

        /// <summary>
        /// Shoelace signed area of a ring (positive = counter-clockwise).
        /// </summary>
        private static double SignedArea(double[][] ring)
        {
            var area = 0.0;
            for (var i = 0; i < ring.Length - 1; i++)
            {
                var current = ring[i];
                var next = ring[i + 1];
                if (current.Length < 2 || next.Length < 2)
                {
                    continue;
                }

                area += (current[0] * next[1]) - (next[0] * current[1]);
            }

            return area / 2.0;
        }

        private static double[][] ReadLine(JsonElement line)
        {
            if (line.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var vertices = new List<double[]>(line.GetArrayLength());
            foreach (var coordinate in line.EnumerateArray())
            {
                if (coordinate.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                double? x = null;
                double? y = null;
                foreach (var ordinate in coordinate.EnumerateArray())
                {
                    if (ordinate.ValueKind != JsonValueKind.Number)
                    {
                        break;
                    }

                    if (x is null)
                    {
                        x = ordinate.GetDouble();
                    }
                    else
                    {
                        y = ordinate.GetDouble();
                        break;
                    }
                }

                if (x is { } xv && y is { } yv)
                {
                    vertices.Add([xv, yv]);
                }
            }

            return [.. vertices];
        }
    }
}
