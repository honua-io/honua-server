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
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

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
    // Warning-severity issues (e.g. a geometry that will be repaired on import) are advisory and
    // must not fail the file; only Error-severity issues make the document invalid (#2743).
    public bool IsValid => !Issues.Any(issue => issue.Severity == ImportValidationSeverity.Error);
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
            var firstChunk = true;

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

                // Skip a leading UTF-8 BOM (EF BB BF) on the first chunk — many real-world
                // GeoJSON files (and anything written via Encoding.UTF8) start with one, and
                // Utf8JsonReader treats it as an invalid start-of-value token otherwise.
                if (firstChunk)
                {
                    firstChunk = false;
                    var span = data.Span;
                    if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                    {
                        data = data[3..];
                    }
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
                if (_limits.MaxSingleFeatureBytes > 0 && remaining > _limits.MaxSingleFeatureBytes)
                {
                    // A single un-closed feature (or pre-feature leftover) has grown past the
                    // per-feature memory budget. Fail fast instead of buffering the whole file
                    // in RAM, which a few crafted concurrent uploads could otherwise use to OOM
                    // the host (#1993).
                    throw new InvalidDataException(
                        $"A single GeoJSON feature (or unparsed leftover) exceeds the maximum " +
                        $"in-memory size of {_limits.MaxSingleFeatureBytes:N0} bytes. The feature " +
                        "object is too large to stream; split the input or raise " +
                        "ImportLimits.MaxSingleFeatureBytes.");
                }

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

            if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "features" && !inFeaturesArray)
            {
                // Next token should be the start of the features array
                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    inFeaturesArray = true;
                    featureDepth = reader.CurrentDepth;
                    lastConsumed = (int)reader.BytesConsumed;
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

            // A single feature object that spans many chunks accumulates here across
            // ProcessJsonChunk calls. Cap its in-memory size so one pathologically large
            // (or never-closing) feature cannot buffer the whole file in RAM (#1993).
            if (_limits.MaxSingleFeatureBytes > 0 &&
                featureBuffer != null &&
                featureBuffer.WrittenCount > _limits.MaxSingleFeatureBytes)
            {
                throw new InvalidDataException(
                    $"A single GeoJSON feature exceeds the maximum in-memory size of " +
                    $"{_limits.MaxSingleFeatureBytes:N0} bytes. The feature object is too large " +
                    "to stream; split the input or raise ImportLimits.MaxSingleFeatureBytes.");
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
        if (geometry is null)
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryInvalid,
                "GeoJSON geometry coordinates are invalid or incomplete.",
                featureIndex,
                "geometry"));
            return issues;
        }

        var validationError = ValidateParsedGeometry(geometry);
        if (validationError != null)
        {
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryInvalid,
                validationError,
                featureIndex,
                "geometry"));
            return issues;
        }

        // Topology validity (self-intersection, ring orientation, hole placement) is reported as a
        // NON-fatal, row-level warning rather than failing the whole file (#2743): the shared
        // per-row validity gate at insertion time repairs (default) or rejects invalid geometry per
        // ImportLimits.GeometryValidityMode. Only areal geometry can be topologically invalid, so
        // points/lines skip the expensive IsValid topology-graph build. Accept mode stores geometry
        // as-is, so nothing is flagged there.
        if (_limits.GeometryValidityMode != Honua.Core.Configuration.ValidationMode.Accept
            && geometry.OgcGeometryType is OgcGeometryType.Polygon
                or OgcGeometryType.MultiPolygon
                or OgcGeometryType.GeometryCollection
            && !geometry.IsValid)
        {
            var action = _limits.GeometryValidityMode == Honua.Core.Configuration.ValidationMode.Strict
                ? "rejected"
                : "repaired";
            issues.Add(ImportValidationIssue.Create(
                ImportValidationErrorCodes.GeometryInvalidRepairable,
                $"Geometry is topologically invalid and will be {action} on import "
                + $"(mode={_limits.GeometryValidityMode}).",
                featureIndex,
                "geometry",
                ImportValidationSeverity.Warning));
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
                var geometries = geometriesElement.EnumerateArray()
                    .Select(ParseGeometry)
                    .Where(geom => geom != null)
                    .Select(geom => geom!)
                    .ToArray();
                return _geometryFactory.CreateGeometryCollection(geometries);
            }
            return null;
        }

        // Broad catch is intentional: coordsElement is untrusted GeoJSON input, and the various
        // Parse* helpers can throw a mix of format/index/topology exceptions for malformed
        // coordinate arrays. Returning null here surfaces as a clean, reported validation issue
        // at the call site ("geometry coordinates are invalid or incomplete") rather than
        // aborting the whole import stream.
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
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
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

        // Topology validity (self-intersection, ring orientation, hole placement) is intentionally
        // NOT rejected here. Previously an invalid ring failed the whole-file preflight, which is
        // inconsistent with every other import reader and with the edit paths' shared repair
        // pipeline. The shared per-row validity gate at insertion time (CreateWkb /
        // ImportLimits.GeometryValidityMode, #2743) now repairs (default), rejects, or accepts
        // invalid geometry per config — so a single bad ring no longer discards the entire file.
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
        var points = coords.EnumerateArray()
            .Select(pointCoords => _geometryFactory.CreatePoint(ParseCoordinate(pointCoords)))
            .ToArray();
        return _geometryFactory.CreateMultiPoint(points);
    }

    private LineString ParseLineString(JsonElement coords)
    {
        var coordinates = ParseCoordinateArray(coords);
        return _geometryFactory.CreateLineString(coordinates);
    }

    private MultiLineString ParseMultiLineString(JsonElement coords)
    {
        var lineStrings = coords.EnumerateArray()
            .Select(ParseCoordinateArray)
            .Where(coordinates => coordinates.Length >= 2)
            .Select(_geometryFactory.CreateLineString)
            .ToArray();
        return _geometryFactory.CreateMultiLineString(lineStrings);
    }

    private Polygon? ParsePolygon(JsonElement coords)
    {
        // RFC 7946 §3.1.6: the first ring is the exterior ring; any further rings
        // are interior rings (holes).  If the exterior ring is degenerate (< 4
        // positions), reject the whole polygon rather than silently promoting a
        // hole into the shell position.
        LinearRing? shell = null;
        var holes = new List<LinearRing>();
        var ringIndex = 0;

        // Not a simple map/select: this loop folds rings into shell/hole state and can
        // short-circuit with an early return on a degenerate exterior ring, so an
        // imperative loop is clearer than a LINQ chain here.
        foreach (var coordinates in (coords.EnumerateArray()).Select(ringCoords => ParseCoordinateArray(ringCoords)))
        {
            if (ringIndex == 0)
            {
                if (coordinates.Length < 4)
                {
                    // Degenerate exterior ring — the polygon cannot be constructed.
                    return null;
                }

                shell = _geometryFactory.CreateLinearRing(coordinates);
            }
            else if (coordinates.Length >= 4)
            {
                holes.Add(_geometryFactory.CreateLinearRing(coordinates));
            }
            // Interior rings with < 4 positions are silently skipped (degenerate
            // holes do not affect exterior membership).

            ringIndex++;
        }

        if (shell is null)
            return _geometryFactory.CreatePolygon();

        return _geometryFactory.CreatePolygon(shell, holes.Count > 0 ? holes.ToArray() : null);
    }

    private MultiPolygon ParseMultiPolygon(JsonElement coords)
    {
        var polygons = coords.EnumerateArray()
            .Select(ParsePolygon)
            .Where(polygon => polygon != null)
            .Select(polygon => polygon!)
            .ToArray();
        return _geometryFactory.CreateMultiPolygon(polygons);
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

            // Locate the deprecated GeoJSON-2008 "crs" member. It is a root-level member that, in
            // conforming documents, precedes the "features" array. Anchor the EPSG scan to the
            // region that belongs to the crs member only: an unanchored scan of the whole 8 KB
            // header can pick up an "EPSG:3857"-looking attribute string inside a feature and
            // wrongly re-stamp the layer's CRS (#2743 rider).
            var crsIndex = headerJson.IndexOf("\"crs\"", StringComparison.OrdinalIgnoreCase);
            if (crsIndex < 0)
                return null;

            // Bound the search at the "features" array (the crs member always precedes it in a
            // conforming document); if it is not visible in the header window, scan to the end.
            var featuresIndex = headerJson.IndexOf("\"features\"", crsIndex, StringComparison.OrdinalIgnoreCase);
            var crsRegion = featuresIndex > crsIndex
                ? headerJson[crsIndex..featuresIndex]
                : headerJson[crsIndex..];

            // Matches both the plain "EPSG:3857" form and the URN "urn:ogc:def:crs:EPSG::3857" form
            // (the double colon is absorbed by the [:\s]* class).
            var epsgMatch = System.Text.RegularExpressions.Regex.Match(
                crsRegion,
                @"EPSG[:\s]*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var srid))
            {
                return srid;
            }
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Broad catch is intentional: this is a best-effort scan of a raw header byte window
            // for an optional, deprecated GeoJSON-2008 "crs" hint. Any failure here (e.g. encoding
            // errors on a malformed header) should fall back to the layer's configured/default CRS
            // rather than aborting the whole import stream.
        }

        return null;
    }
}
