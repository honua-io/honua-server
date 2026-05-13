// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Memory;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// State for tracking JSON processing across chunks
/// </summary>
internal sealed record JsonProcessingState
{
    public bool InFeaturesArray { get; init; }
    public int FeatureDepth { get; init; }
    public bool IsCollectingFeature { get; init; }
    public int FeatureStartIndex { get; init; } = -1;
    public ArrayBufferWriter<byte>? FeatureBuffer { get; init; }
    public bool IsComplete { get; init; }
}

internal sealed record GeoJsonValidationResult(int FeatureCount, IReadOnlyList<ImportValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Memory-efficient streaming GeoJSON reader that processes features incrementally.
/// Uses Utf8JsonReader for low-allocation parsing without loading the entire file into memory.
/// </summary>
internal sealed class StreamingGeoJsonReader
{
    private readonly ImportLimits _limits;
    private readonly GeometryFactory _geometryFactory;
    private const int MaxValidationIssues = 100;
    private static readonly HashSet<string> _knownGeometryTypes = new(StringComparer.Ordinal)
    {
        "Point",
        "MultiPoint",
        "LineString",
        "MultiLineString",
        "Polygon",
        "MultiPolygon",
        "GeometryCollection"
    };

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
        var buffer = MemoryPool.RentByteArray(_limits.StreamBufferSize);
        byte[]? leftoverBuffer = null;
        var leftoverLength = 0;
        try
        {
            var jsonReaderState = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var featureCount = 0;
            var processingState = new JsonProcessingState();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read more data from stream
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0 && leftoverLength == 0)
                    break;

                // Combine leftover with new data
                ReadOnlyMemory<byte> data;
                byte[]? combinedBuffer = null;
                if (leftoverLength == 0)
                {
                    data = buffer.AsMemory(0, bytesRead);
                }
                else
                {
                    combinedBuffer = MemoryPool.RentByteArray(leftoverLength + bytesRead);
                    leftoverBuffer!.AsSpan(0, leftoverLength).CopyTo(combinedBuffer);
                    buffer.AsSpan(0, bytesRead).CopyTo(combinedBuffer.AsSpan(leftoverLength, bytesRead));
                    data = combinedBuffer.AsMemory(0, leftoverLength + bytesRead);
                }

                // Process chunk synchronously to avoid ref struct issues
                var (features, newState, newProcessingState, consumed) = ProcessJsonChunk(data, jsonReaderState, processingState);

                jsonReaderState = newState;
                processingState = newProcessingState;

                // Yield all features found in this chunk
                foreach (var feature in features)
                {
                    featureCount++;
                    yield return feature;

                    // Check feature limit
                    if (_limits.MaxFeaturesPerFile > 0 && featureCount >= _limits.MaxFeaturesPerFile)
                    {
                        break;
                    }
                }

                var remaining = data.Length - consumed;
                if (remaining > 0)
                {
                    if (leftoverBuffer == null || leftoverBuffer.Length < remaining)
                    {
                        if (leftoverBuffer != null)
                        {
                            MemoryPool.ReturnByteArray(leftoverBuffer, clearArray: false);
                        }

                        leftoverBuffer = MemoryPool.RentByteArray(remaining);
                    }

                    data.Span.Slice(consumed, remaining).CopyTo(leftoverBuffer);
                    leftoverLength = remaining;
                }
                else
                {
                    if (leftoverBuffer != null)
                    {
                        MemoryPool.ReturnByteArray(leftoverBuffer, clearArray: false);
                        leftoverBuffer = null;
                    }

                    leftoverLength = 0;
                }

                if (combinedBuffer != null)
                {
                    MemoryPool.ReturnByteArray(combinedBuffer, clearArray: false);
                }

                if (_limits.MaxFeaturesPerFile > 0 && featureCount >= _limits.MaxFeaturesPerFile)
                {
                    break;
                }

                // If we've finished processing the features array, break
                if (processingState.IsComplete)
                {
                    break;
                }
            }
        }
        finally
        {
            MemoryPool.ReturnByteArray(buffer);
            if (leftoverBuffer != null)
            {
                MemoryPool.ReturnByteArray(leftoverBuffer, clearArray: false);
            }
        }
    }

    public async Task<GeoJsonValidationResult> ValidateAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ImportValidationIssue>();
        var featureCount = 0;

        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
                if (stream.Length > _limits.MaxMemoryBytes)
                {
                    issues.Add(ImportValidationIssue.Create(
                        ImportValidationErrorCodes.GeoJsonValidationTooLarge,
                        $"GeoJSON preflight validation is limited to {_limits.MaxMemoryBytes:N0} bytes.",
                        field: "file"));

                    return new GeoJsonValidationResult(featureCount, issues);
                }
            }

            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                },
                cancellationToken);

            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var rootTypeElement) ||
                !string.Equals(rootTypeElement.GetString(), "FeatureCollection", StringComparison.Ordinal))
            {
                issues.Add(ImportValidationIssue.Create(
                    ImportValidationErrorCodes.InvalidGeoJson,
                    "GeoJSON root object must have type \"FeatureCollection\".",
                    field: "type"));
            }

            if (!root.TryGetProperty("features", out var featuresElement) ||
                featuresElement.ValueKind != JsonValueKind.Array)
            {
                issues.Add(ImportValidationIssue.Create(
                    ImportValidationErrorCodes.EmptyDataset,
                    "GeoJSON FeatureCollection does not contain a features array.",
                    field: "features"));
            }
            else
            {
                foreach (var featureElement in featuresElement.EnumerateArray())
                {
                    featureCount++;
                    if (issues.Count < MaxValidationIssues)
                    {
                        issues.AddRange(ValidateFeature(featureElement, featureCount)
                            .Take(MaxValidationIssues - issues.Count));
                    }

                    if (_limits.MaxFeaturesPerFile > 0 && featureCount >= _limits.MaxFeaturesPerFile)
                    {
                        break;
                    }
                }
            }

            if (featureCount == 0 && issues.All(issue => issue.Code != ImportValidationErrorCodes.EmptyDataset))
            {
                issues.Add(ImportValidationIssue.Create(
                    ImportValidationErrorCodes.EmptyDataset,
                    "GeoJSON FeatureCollection does not contain any features.",
                    field: "features"));
            }
        }
        catch (JsonException)
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.InvalidGeoJson,
                "GeoJSON document is not valid JSON."));
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
        }

        return new GeoJsonValidationResult(featureCount, issues);
    }

    /// <summary>
    /// Process a chunk of JSON data synchronously to avoid ref struct issues
    /// </summary>
    private (List<IFeature> features, JsonReaderState newState, JsonProcessingState newProcessingState, int consumed) ProcessJsonChunk(
        ReadOnlyMemory<byte> data,
        JsonReaderState jsonReaderState,
        JsonProcessingState processingState)
    {
        var features = new List<IFeature>();
        var reader = new Utf8JsonReader(data.Span, false, jsonReaderState);
        var lastConsumed = 0;

        var inFeaturesArray = processingState.InFeaturesArray;
        var featureDepth = processingState.FeatureDepth;
        var isCollectingFeature = processingState.IsCollectingFeature;
        var featureStartIndex = processingState.FeatureStartIndex;
        var featureBuffer = processingState.FeatureBuffer;
        var isComplete = processingState.IsComplete;
        var featureBufferOffset = 0;

        void BufferFeatureBytes(int endExclusive)
        {
            if (featureBuffer == null || endExclusive <= featureBufferOffset)
            {
                return;
            }

            featureBuffer.Write(data.Span.Slice(featureBufferOffset, endExclusive - featureBufferOffset));
            featureBufferOffset = endExclusive;
        }

        while (!isComplete)
        {
            if (!reader.Read())
            {
                break;
            }

            lastConsumed = (int)reader.BytesConsumed;

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
                        lastConsumed = (int)reader.BytesConsumed;
                    }
                }
            }
            else if (inFeaturesArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == featureDepth + 1)
                {
                    // Start of a feature
                    isCollectingFeature = true;
                    featureStartIndex = (int)reader.TokenStartIndex;
                }
                else if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == featureDepth + 1)
                {
                    // End of a feature - parse it
                    if (isCollectingFeature)
                    {
                        var featureEnd = (int)reader.BytesConsumed;
                        Feature? feature;
                        if (featureBuffer != null)
                        {
                            BufferFeatureBytes(featureEnd);
                            feature = ParseFeature(featureBuffer.WrittenMemory);
                        }
                        else
                        {
                            var featureMemory = data.Slice(featureStartIndex, featureEnd - featureStartIndex);
                            feature = ParseFeature(featureMemory);
                        }

                        if (feature != null)
                        {
                            features.Add(feature);
                        }

                        isCollectingFeature = false;
                        featureStartIndex = -1;
                        featureBuffer = null;
                        featureBufferOffset = 0;
                    }
                }
                else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == featureDepth)
                {
                    // End of features array
                    isComplete = true;
                    break;
                }
            }
        }

        if (isCollectingFeature)
        {
            if (featureBuffer == null && featureStartIndex >= 0 && featureStartIndex < data.Length)
            {
                featureBuffer = new ArrayBufferWriter<byte>(data.Length - featureStartIndex);
                featureBuffer.Write(data.Span.Slice(featureStartIndex));
            }
            else
            {
                BufferFeatureBytes(data.Length);
            }
        }

        var newProcessingState = new JsonProcessingState
        {
            InFeaturesArray = inFeaturesArray,
            FeatureDepth = featureDepth,
            IsCollectingFeature = isCollectingFeature,
            FeatureStartIndex = featureStartIndex,
            FeatureBuffer = featureBuffer,
            IsComplete = isComplete
        };

        return (features, reader.CurrentState, newProcessingState, lastConsumed);
    }

    private List<ImportValidationIssue> ValidateFeature(JsonElement root, int featureIndex)
    {
        var issues = new List<ImportValidationIssue>(1);

        if (!root.TryGetProperty("type", out var typeElement) ||
            !string.Equals(typeElement.GetString(), "Feature", StringComparison.Ordinal))
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.InvalidGeoJson,
                "GeoJSON features must have type \"Feature\".",
                featureIndex,
                "type"));
            return issues;
        }

        if (!root.TryGetProperty("geometry", out var geometryElement) ||
            geometryElement.ValueKind == JsonValueKind.Null)
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryMissing,
                "GeoJSON feature is missing geometry.",
                featureIndex,
                "geometry"));
            return issues;
        }

        if (geometryElement.ValueKind != JsonValueKind.Object)
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryInvalid,
                "GeoJSON feature geometry must be an object.",
                featureIndex,
                "geometry"));
            return issues;
        }

        if (!geometryElement.TryGetProperty("type", out var geometryTypeElement) ||
            string.IsNullOrWhiteSpace(geometryTypeElement.GetString()))
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryMissing,
                "GeoJSON feature geometry is missing a type.",
                featureIndex,
                "geometry.type"));
            return issues;
        }

        var geometryType = geometryTypeElement.GetString()!;
        if (!_knownGeometryTypes.Contains(geometryType))
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryUnknownType,
                $"GeoJSON geometry type \"{geometryType}\" is not supported.",
                featureIndex,
                "geometry.type"));
            return issues;
        }

        var geometry = ParseGeometry(geometryElement);
        var validationError = geometry == null
            ? "GeoJSON geometry coordinates are invalid or incomplete."
            : ValidateParsedGeometry(geometry);
        if (validationError != null)
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryInvalid,
                validationError,
                featureIndex,
                "geometry"));
        }

        return issues;
    }

    /// <summary>
    /// Parse a single GeoJSON feature from a byte span.
    /// </summary>
    private Feature? ParseFeature(ReadOnlyMemory<byte> featureJson)
    {
        try
        {
            using var document = JsonDocument.Parse(featureJson);
            var root = document.RootElement;

            // Verify it's a Feature
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() != "Feature")
            {
                return null;
            }

            // Parse geometry
            NtsGeometry? geometry = null;
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
    private NtsGeometry? ParseGeometry(JsonElement geometryElement)
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
                var geometries = new List<NtsGeometry>();
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

    private string? ValidateParsedGeometry(NtsGeometry geometry)
    {
        if (geometry.IsEmpty)
        {
            return "GeoJSON geometry is empty.";
        }

        if (geometry.NumPoints > _limits.MaxVertices)
        {
            return $"Geometry vertex count ({geometry.NumPoints:N0}) exceeds maximum allowed ({_limits.MaxVertices:N0}).";
        }

        var ringCount = CountRings(geometry);
        if (ringCount > _limits.MaxRings)
        {
            return $"Geometry ring count ({ringCount:N0}) exceeds maximum allowed ({_limits.MaxRings:N0}).";
        }

        if (!ValidateCoordinates(geometry))
        {
            return "Geometry contains invalid coordinates (NaN or Infinity).";
        }

        if (!geometry.IsValid)
        {
            return "Geometry topology is invalid.";
        }

        return null;
    }

    private static int CountRings(NtsGeometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => multiPolygon.Geometries
                .OfType<Polygon>()
                .Sum(p => 1 + p.NumInteriorRings),
            GeometryCollection collection => collection.Geometries.Sum(CountRings),
            _ => 0
        };
    }

    private static bool ValidateCoordinates(NtsGeometry geometry)
    {
        foreach (var coord in geometry.Coordinates)
        {
            if (double.IsNaN(coord.X) || double.IsInfinity(coord.X) ||
                double.IsNaN(coord.Y) || double.IsInfinity(coord.Y))
            {
                return false;
            }

            if (!double.IsNaN(coord.Z) && double.IsInfinity(coord.Z))
            {
                return false;
            }
        }

        return true;
    }

    private Point ParsePoint(JsonElement coords)
    {
        var coordinate = ParseCoordinate(coords);
        return _geometryFactory.CreatePoint(coordinate);
    }

    private MultiPoint ParseMultiPoint(JsonElement coords)
    {
        var points = new List<Point>();
        foreach (var pointCoords in coords.EnumerateArray())
        {
            var coord = ParseCoordinate(pointCoords);
            points.Add(_geometryFactory.CreatePoint(coord));
        }
        return _geometryFactory.CreateMultiPoint(points.ToArray());
    }

    private LineString ParseLineString(JsonElement coords)
    {
        var coordinates = ParseCoordinateArray(coords);
        return _geometryFactory.CreateLineString(coordinates);
    }

    private MultiLineString ParseMultiLineString(JsonElement coords)
    {
        var lineStrings = new List<LineString>();
        foreach (var lineCoords in coords.EnumerateArray())
        {
            var coordinates = ParseCoordinateArray(lineCoords);
            if (coordinates.Length < 2)
                continue;
            lineStrings.Add(_geometryFactory.CreateLineString(coordinates));
        }
        return _geometryFactory.CreateMultiLineString(lineStrings.ToArray());
    }

    private Polygon ParsePolygon(JsonElement coords)
    {
        var rings = new List<LinearRing>();
        foreach (var ringCoords in coords.EnumerateArray())
        {
            var coordinates = ParseCoordinateArray(ringCoords);
            if (coordinates.Length < 4)
                continue;
            rings.Add(_geometryFactory.CreateLinearRing(coordinates));
        }

        if (rings.Count == 0)
            return _geometryFactory.CreatePolygon();

        var shell = rings[0];
        var holes = rings.Count > 1 ? rings.Skip(1).ToArray() : null;
        return _geometryFactory.CreatePolygon(shell, holes);
    }

    private MultiPolygon ParseMultiPolygon(JsonElement coords)
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

    private static Coordinate ParseCoordinate(JsonElement coords)
    {
        var enumerator = coords.EnumerateArray();
        if (!enumerator.MoveNext())
        {
            throw new FormatException("Coordinate must contain at least two values.");
        }

        var x = enumerator.Current.GetDouble();
        if (!enumerator.MoveNext())
        {
            throw new FormatException("Coordinate must contain at least two values.");
        }

        var y = enumerator.Current.GetDouble();
        if (enumerator.MoveNext())
        {
            var z = enumerator.Current.GetDouble();
            return new CoordinateZ(x, y, z);
        }

        return new Coordinate(x, y);
    }

    private static Coordinate[] ParseCoordinateArray(JsonElement coords)
    {
        // Pre-allocate array to avoid List growth and ToArray() allocation
        var coordCount = coords.GetArrayLength();
        var coordinates = new Coordinate[coordCount];

        var index = 0;
        foreach (var coord in coords.EnumerateArray())
        {
            coordinates[index++] = ParseCoordinate(coord);
        }
        return coordinates;
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
            JsonValueKind.Array => element.Clone(),
            JsonValueKind.Object => element.Clone(),
            _ => null
        };
    }

    /// <summary>
    /// Detect CRS from GeoJSON stream header without reading the entire file.
    /// Reads only the beginning of the file to find the CRS property.
    /// </summary>
    public static async Task<int?> DetectCrsAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Read only the first few KB to find CRS
        const int headerSize = 8192;
        using var bufferRental = GeometryMemoryManager.RentWkbBuffer(headerSize);
        var buffer = bufferRental.Buffer;
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, headerSize), cancellationToken);

        if (bytesRead == 0)
            return null;

        // Reset stream position if possible
        if (stream.CanSeek)
            stream.Position = 0;

        try
        {
            // Parse the header portion
            var headerJson = Encoding.UTF8.GetString(buffer.AsSpan(0, bytesRead));

            // Look for CRS property in the beginning of the document
            // The CRS is typically at the root level, so we can use simple string search
            if (!headerJson.Contains("\"crs\"", StringComparison.OrdinalIgnoreCase))
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
