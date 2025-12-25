// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Memory-efficient streaming GeoJSON reader that processes features incrementally.
/// Uses Utf8JsonReader for low-allocation parsing without loading the entire file into memory.
/// </summary>
internal sealed class StreamingGeoJsonReader
{
    private readonly ImportLimits _limits;
    private readonly GeometryFactory _geometryFactory;
    private static readonly WKTReader WktReader = new();

    public StreamingGeoJsonReader(ImportLimits? limits = null)
    {
        _limits = limits ?? ImportLimits.Default;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
    }

    /// <summary>
    /// Stream features from a GeoJSON file asynchronously.
    /// Features are yielded one at a time to maintain constant memory usage.
    /// </summary>
    public async IAsyncEnumerable<IFeature> ReadFeaturesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_limits.StreamBufferSize);
        try
        {
            var jsonReaderState = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var leftover = ReadOnlyMemory<byte>.Empty;
            var featureCount = 0;
            var inFeaturesArray = false;
            var featureDepth = 0;
            var featureStartIndex = -1;
            var currentFeatureBytes = new List<byte>();
            var isCollectingFeature = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read more data from stream
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0 && leftover.IsEmpty)
                    break;

                // Combine leftover with new data
                ReadOnlyMemory<byte> data;
                if (leftover.IsEmpty)
                {
                    data = buffer.AsMemory(0, bytesRead);
                }
                else
                {
                    var combined = new byte[leftover.Length + bytesRead];
                    leftover.CopyTo(combined);
                    buffer.AsMemory(0, bytesRead).CopyTo(combined.AsMemory(leftover.Length));
                    data = combined;
                }

                var reader = new Utf8JsonReader(data.Span, bytesRead == 0, jsonReaderState);

                var lastConsumed = 0L;

                while (reader.Read())
                {
                    lastConsumed = reader.BytesConsumed;

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var propertyName = reader.GetString();
                        if (propertyName == "features" && !inFeaturesArray)
                        {
                            // Next token should be the start of the features array
                            if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                            {
                                inFeaturesArray = true;
                                featureDepth = reader.CurrentDepth;
                            }
                        }
                    }
                    else if (inFeaturesArray)
                    {
                        if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == featureDepth + 1)
                        {
                            // Start of a feature
                            isCollectingFeature = true;
                            currentFeatureBytes.Clear();
                            featureStartIndex = (int)reader.TokenStartIndex;
                        }
                        else if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == featureDepth + 1)
                        {
                            // End of a feature - parse it
                            if (isCollectingFeature)
                            {
                                var featureEnd = (int)reader.BytesConsumed;
                                var featureSpan = data.Span.Slice(featureStartIndex, featureEnd - featureStartIndex);

                                var feature = ParseFeature(featureSpan);
                                if (feature != null)
                                {
                                    featureCount++;
                                    yield return feature;

                                    // Check feature limit
                                    if (_limits.MaxFeaturesPerFile > 0 && featureCount >= _limits.MaxFeaturesPerFile)
                                    {
                                        yield break;
                                    }
                                }

                                isCollectingFeature = false;
                            }
                        }
                        else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == featureDepth)
                        {
                            // End of features array
                            inFeaturesArray = false;
                            yield break;
                        }
                    }
                }

                jsonReaderState = reader.CurrentState;

                // Keep unconsumed data for next iteration
                if (lastConsumed < data.Length)
                {
                    leftover = data.Slice((int)lastConsumed).ToArray();
                }
                else
                {
                    leftover = ReadOnlyMemory<byte>.Empty;
                }

                // Yield control to avoid blocking
                await Task.Yield();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parse a single GeoJSON feature from a byte span.
    /// </summary>
    private IFeature? ParseFeature(ReadOnlySpan<byte> featureJson)
    {
        try
        {
            using var document = JsonDocument.Parse(featureJson.ToArray());
            var root = document.RootElement;

            // Verify it's a Feature
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() != "Feature")
            {
                return null;
            }

            // Parse geometry
            Geometry? geometry = null;
            if (root.TryGetProperty("geometry", out var geometryElement) &&
                geometryElement.ValueKind != JsonValueKind.Null)
            {
                geometry = ParseGeometry(geometryElement);
            }

            // Parse properties
            var attributes = new AttributesTable();
            if (root.TryGetProperty("properties", out var propertiesElement) &&
                propertiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in propertiesElement.EnumerateObject())
                {
                    var value = GetPropertyValue(prop.Value);
                    attributes.Add(prop.Name, value);
                }
            }

            // Parse id if present
            if (root.TryGetProperty("id", out var idElement))
            {
                var idValue = GetPropertyValue(idElement);
                if (idValue != null && !attributes.Exists("id"))
                {
                    attributes.Add("id", idValue);
                }
            }

            return new Feature(geometry, attributes);
        }
        catch (JsonException)
        {
            // Invalid feature JSON - return null to skip
            return null;
        }
    }

    /// <summary>
    /// Parse a GeoJSON geometry element into a NetTopologySuite Geometry.
    /// </summary>
    private Geometry? ParseGeometry(JsonElement geometryElement)
    {
        if (geometryElement.ValueKind == JsonValueKind.Null)
            return null;

        if (!geometryElement.TryGetProperty("type", out var typeElement))
            return null;

        var geometryType = typeElement.GetString();
        if (string.IsNullOrEmpty(geometryType))
            return null;

        if (!geometryElement.TryGetProperty("coordinates", out var coordsElement))
        {
            // Handle GeometryCollection
            if (geometryType == "GeometryCollection" &&
                geometryElement.TryGetProperty("geometries", out var geometriesElement))
            {
                var geometries = new List<Geometry>();
                foreach (var geomElement in geometriesElement.EnumerateArray())
                {
                    var geom = ParseGeometry(geomElement);
                    if (geom != null)
                        geometries.Add(geom);
                }
                return _geometryFactory.CreateGeometryCollection(geometries.ToArray());
            }
            return null;
        }

        try
        {
            return geometryType switch
            {
                "Point" => ParsePoint(coordsElement),
                "MultiPoint" => ParseMultiPoint(coordsElement),
                "LineString" => ParseLineString(coordsElement),
                "MultiLineString" => ParseMultiLineString(coordsElement),
                "Polygon" => ParsePolygon(coordsElement),
                "MultiPolygon" => ParseMultiPolygon(coordsElement),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private Geometry ParsePoint(JsonElement coords)
    {
        var coordinate = ParseCoordinate(coords);
        return _geometryFactory.CreatePoint(coordinate);
    }

    private Geometry ParseMultiPoint(JsonElement coords)
    {
        var points = new List<Point>();
        foreach (var pointCoords in coords.EnumerateArray())
        {
            var coord = ParseCoordinate(pointCoords);
            points.Add(_geometryFactory.CreatePoint(coord));
        }
        return _geometryFactory.CreateMultiPoint(points.ToArray());
    }

    private Geometry ParseLineString(JsonElement coords)
    {
        var coordinates = ParseCoordinateArray(coords);
        return _geometryFactory.CreateLineString(coordinates);
    }

    private Geometry ParseMultiLineString(JsonElement coords)
    {
        var lineStrings = new List<LineString>();
        foreach (var lineCoords in coords.EnumerateArray())
        {
            var coordinates = ParseCoordinateArray(lineCoords);
            lineStrings.Add(_geometryFactory.CreateLineString(coordinates));
        }
        return _geometryFactory.CreateMultiLineString(lineStrings.ToArray());
    }

    private Geometry ParsePolygon(JsonElement coords)
    {
        var rings = new List<LinearRing>();
        foreach (var ringCoords in coords.EnumerateArray())
        {
            var coordinates = ParseCoordinateArray(ringCoords);
            rings.Add(_geometryFactory.CreateLinearRing(coordinates));
        }

        if (rings.Count == 0)
            return _geometryFactory.CreatePolygon();

        var shell = rings[0];
        var holes = rings.Count > 1 ? rings.Skip(1).ToArray() : null;
        return _geometryFactory.CreatePolygon(shell, holes);
    }

    private Geometry ParseMultiPolygon(JsonElement coords)
    {
        var polygons = new List<Polygon>();
        foreach (var polyCoords in coords.EnumerateArray())
        {
            var polygon = ParsePolygon(polyCoords) as Polygon;
            if (polygon != null)
                polygons.Add(polygon);
        }
        return _geometryFactory.CreateMultiPolygon(polygons.ToArray());
    }

    private Coordinate ParseCoordinate(JsonElement coords)
    {
        var coordArray = coords.EnumerateArray().ToArray();
        var x = coordArray[0].GetDouble();
        var y = coordArray[1].GetDouble();

        if (coordArray.Length > 2)
        {
            var z = coordArray[2].GetDouble();
            return new CoordinateZ(x, y, z);
        }

        return new Coordinate(x, y);
    }

    private Coordinate[] ParseCoordinateArray(JsonElement coords)
    {
        var coordinates = new List<Coordinate>();
        foreach (var coord in coords.EnumerateArray())
        {
            coordinates.Add(ParseCoordinate(coord));
        }
        return coordinates.ToArray();
    }

    private static object? GetPropertyValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.ToString(),
            JsonValueKind.Object => element.ToString(),
            _ => null
        };
    }

    /// <summary>
    /// Detect CRS from GeoJSON stream header without reading the entire file.
    /// Reads only the beginning of the file to find the CRS property.
    /// </summary>
    public async Task<int?> DetectCrsAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Read only the first few KB to find CRS
        const int headerSize = 8192;
        var buffer = new byte[headerSize];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, headerSize), cancellationToken);

        if (bytesRead == 0)
            return null;

        // Reset stream position if possible
        if (stream.CanSeek)
            stream.Position = 0;

        try
        {
            // Parse the header portion
            var headerJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Look for CRS property in the beginning of the document
            // The CRS is typically at the root level, so we can use simple string search
            var crsIndex = headerJson.IndexOf("\"crs\"", StringComparison.OrdinalIgnoreCase);
            if (crsIndex == -1)
                return null;

            // Try to extract EPSG code from the CRS
            var epsgMatch = System.Text.RegularExpressions.Regex.Match(
                headerJson,
                @"EPSG[:\s]*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var srid))
            {
                return srid;
            }
        }
        catch
        {
            // Ignore parsing errors for CRS detection
        }

        return null;
    }
}
