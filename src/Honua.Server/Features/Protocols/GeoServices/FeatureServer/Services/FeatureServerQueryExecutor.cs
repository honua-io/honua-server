// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Npgsql;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Executes FeatureServer queries and handles streaming responses.
/// </summary>
internal sealed class FeatureServerQueryExecutor
{
    private const int FlushInterval = 64;
    private readonly IFeatureReader _featureReader;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly StreamingQueryFormatter _streamingFormatter;

    public FeatureServerQueryExecutor(
        IFeatureReader featureReader,
        IStreamingFeatureStore streamingFeatureStore,
        StreamingQueryFormatter streamingFormatter)
    {
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _streamingFeatureStore = streamingFeatureStore ?? throw new ArgumentNullException(nameof(streamingFeatureStore));
        _streamingFormatter = streamingFormatter ?? throw new ArgumentNullException(nameof(streamingFormatter));
    }

    public bool SupportsGeobufOutput => _featureReader is IGeobufFeatureStore;
    public bool SupportsRawGeoServicesPointOutput => _featureReader is IPagedRawGeoServicesFeatureStore;

    public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken)
        => _featureReader.CountAsync(layerId, query, cancellationToken);

    public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken)
        => _featureReader.GetExtentAsync(layerId, query, cancellationToken);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
        => _featureReader.QueryStatisticsAsync(layerId, query, cancellationToken);

    public async Task<QueryResult<Feature>> QueryWithValidationAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ShouldUsePagedQuery(query) && _featureReader is IPagedFeatureReader pagedFeatureReader)
            {
                var pagedResult = await pagedFeatureReader.QueryPageAsync(layerId, query, cancellationToken);
                return QueryResult<Feature>.Create(
                    totalCount: pagedResult.TotalCount ?? GetLowerBoundTotalCount(pagedResult.Items.Length, pagedResult.HasMoreResults),
                    items: pagedResult.Items,
                    hasMoreResults: pagedResult.HasMoreResults);
            }

            return await _featureReader.QueryAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid query.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Invalid query format.", ex);
        }
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException("Invalid query syntax.", ex);
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException("Query execution failed.", ex);
        }
    }

    public async Task<(ReadOnlyMemory<byte> Payload, int Count)> QueryRawGeoServicesPointJsonWithValidationAsync(
        int layerId,
        FeatureQuery query,
        LayerDefinition layer,
        int? outputSrid,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_featureReader is not IPagedRawGeoServicesFeatureStore rawGeoServicesFeatureStore)
            {
                throw new InvalidOperationException("Raw GeoServices output is not supported by the configured feature store.");
            }

            var result = await rawGeoServicesFeatureStore.QueryGeoServicesRawPointPageAsync(
                layerId,
                query,
                cancellationToken).ConfigureAwait(false);

            return (CreateRawGeoServicesPointPayload(result, layer, outputSrid), result.Items.Length);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid query.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Invalid query format.", ex);
        }
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException("Invalid query syntax.", ex);
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException("Query execution failed.", ex);
        }
    }

    private static ReadOnlyMemory<byte> CreateRawGeoServicesPointPayload(
        PagedQueryResult<RawGeoServicesFeature> result,
        LayerDefinition layer,
        int? outputSrid)
    {
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(layer);
        var queryFields = QueryFormatter.BuildQueryFields(layer, outFields: null, objectIdFieldName);
        var allowedAttributeNames = queryFields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayFieldName = QueryFormatter.ResolveDisplayFieldName(queryFields, objectIdFieldName);
        var srid = outputSrid ?? layer.SpatialReference.Wkid;
        var buffer = new ArrayBufferWriter<byte>(EstimateRawGeoServicesPointPayloadCapacity(result, queryFields));
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("geometryType", "esriGeometryPoint");
        writer.WritePropertyName("spatialReference");
        JsonSerializer.Serialize(
            writer,
            new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid },
            FeatureServerJsonContext.Default.GeoServicesSpatialReference);
        writer.WriteString("displayFieldName", displayFieldName);
        writer.WritePropertyName("fields");
        JsonSerializer.Serialize(writer, queryFields, FeatureServerJsonContext.Default.GeoServicesFieldInfoArray);
        writer.WriteString("objectIdFieldName", objectIdFieldName);
        writer.WriteStartArray("features");

        foreach (var feature in result.Items)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("attributes");
            WriteRawGeoServicesAttributes(writer, feature, allowedAttributeNames, objectIdFieldName);

            writer.WritePropertyName("geometry");
            if (feature.X.HasValue && feature.Y.HasValue)
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", feature.X.Value);
                writer.WriteNumber("y", feature.Y.Value);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (result.HasMoreResults)
        {
            writer.WriteBoolean("exceededTransferLimit", true);
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenMemory;
    }

    private static void WriteRawGeoServicesAttributes(
        Utf8JsonWriter writer,
        RawGeoServicesFeature feature,
        IReadOnlySet<string> allowedAttributeNames,
        string objectIdFieldName)
    {
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(feature.AttributesJson))
        {
            WriteDeclaredRawAttributes(writer, feature.AttributesJson, allowedAttributeNames, objectIdFieldName);
        }

        writer.WritePropertyName(objectIdFieldName);
        WriteRawGeoServicesObjectIdValue(writer, feature);
        writer.WriteEndObject();
    }

    private static void WriteDeclaredRawAttributes(
        Utf8JsonWriter writer,
        string attributesJson,
        IReadOnlySet<string> allowedAttributeNames,
        string objectIdFieldName)
    {
        using var document = JsonDocument.Parse(attributesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!ShouldIncludeRawGeoServicesAttribute(property.Name, allowedAttributeNames, objectIdFieldName))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
    }

    private static bool ShouldIncludeRawGeoServicesAttribute(
        string fieldName,
        IReadOnlySet<string> allowedAttributeNames,
        string objectIdFieldName)
        => !fieldName.StartsWith("__", StringComparison.Ordinal) &&
           !fieldName.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
           allowedAttributeNames.Contains(fieldName);

    private static void WriteRawGeoServicesObjectIdValue(Utf8JsonWriter writer, RawGeoServicesFeature feature)
    {
        if (string.IsNullOrWhiteSpace(feature.PublicIdJson))
        {
            writer.WriteNumberValue(feature.Id);
            return;
        }

        using var document = JsonDocument.Parse(feature.PublicIdJson);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            root.WriteTo(writer);
            return;
        }

        writer.WriteNumberValue(feature.Id);
    }

    private static int EstimateRawGeoServicesPointPayloadCapacity(
        PagedQueryResult<RawGeoServicesFeature> result,
        GeoServicesFieldInfo[] queryFields)
    {
        const int MinimumCapacity = 16 * 1024;
        const int FixedPayloadOverhead = 512;
        const int FieldOverhead = 128;
        const int FeatureOverhead = 96;

        long estimated = FixedPayloadOverhead + (queryFields.Length * FieldOverhead);
        foreach (var feature in result.Items)
        {
            estimated += (feature.AttributesJson?.Length ?? 32) + FeatureOverhead;
        }

        return estimated >= int.MaxValue
            ? int.MaxValue
            : Math.Max(MinimumCapacity, (int)estimated);
    }

    public async Task<byte[]?> QueryFlatGeobufWithValidationAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _featureReader.QueryFlatGeobufAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid query.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Invalid query format.", ex);
        }
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException("Invalid query syntax.", ex);
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException("Query execution failed.", ex);
        }
    }

    public async Task<byte[]?> QueryGeobufWithValidationAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_featureReader is not IGeobufFeatureStore geobufFeatureStore)
            {
                throw new InvalidOperationException("Geobuf output is not supported by the configured feature store.");
            }

            return await geobufFeatureStore.QueryGeobufAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid query.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Invalid query format.", ex);
        }
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException("Invalid query syntax.", ex);
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException("Query execution failed.", ex);
        }
    }

    public async Task StreamQueryAsync(
        int layerId,
        FeatureQuery query,
        LayerDefinition layer,
        QueryParameters queryParams,
        int? outputSrid,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var preparedStream = await PrepareFeatureStreamAsync(layerId, query, cancellationToken);

        string[]? outFields = string.IsNullOrEmpty(queryParams.OutFields)
            ? null
            : [.. queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

        var format = queryParams.F ?? "json";
        var contentType = format.ToLowerInvariant() switch
        {
            "geojson" => "application/geo+json",
            _ => "application/json"
        };

        context.Response.ContentType = contentType;
        context.Response.StatusCode = StatusCodes.Status200OK;

        if (string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase))
        {
            await _streamingFormatter.StreamAsGeoJsonAsync(
                preparedStream.Features,
                layer,
                queryParams.ReturnGeometry,
                queryParams.ReturnZ,
                queryParams.ReturnM,
                queryParams.GeometryPrecision,
                queryParams.MaxAllowableOffset,
                outFields,
                preparedStream.HasMoreResults,
                context.Response.BodyWriter,
                cancellationToken);
        }
        else
        {
            await _streamingFormatter.StreamAsGeoServicesJsonAsync(
                preparedStream.Features,
                layer,
                queryParams.ReturnGeometry,
                outputSrid,
                queryParams.ReturnZ,
                queryParams.ReturnM,
                queryParams.GeometryPrecision,
                queryParams.MaxAllowableOffset,
                outFields,
                preparedStream.HasMoreResults,
                context.Response.BodyWriter,
                cancellationToken);
        }

        await context.Response.CompleteAsync();
    }

    private async Task<PreparedFeatureStream> PrepareFeatureStreamAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            return new PreparedFeatureStream(
                _streamingFeatureStore.StreamFeaturesAsync(layerId, query, cancellationToken),
                HasMoreResults: false);
        }

        var requestedLimit = Math.Max(0, query.Limit.Value);
        var probeLimit = checked(requestedLimit + 1);
        var probeQuery = query with { Limit = probeLimit };
        var bufferedFeatures = new List<Feature>(probeLimit);

        await foreach (var feature in _streamingFeatureStore.StreamFeaturesAsync(layerId, probeQuery, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            bufferedFeatures.Add(feature);
        }

        var hasMoreResults = bufferedFeatures.Count > requestedLimit;
        if (hasMoreResults)
        {
            bufferedFeatures.RemoveAt(bufferedFeatures.Count - 1);
        }

        return new PreparedFeatureStream(
            StreamBufferedFeaturesAsync(bufferedFeatures, cancellationToken),
            hasMoreResults);
    }

    private static async IAsyncEnumerable<Feature> StreamBufferedFeaturesAsync(
        IReadOnlyList<Feature> features,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
        }
    }

    private readonly record struct PreparedFeatureStream(
        IAsyncEnumerable<Feature> Features,
        bool HasMoreResults);

    public async Task StreamIdsAsync(
        int layerId,
        FeatureQuery query,
        string objectIdFieldName,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status200OK;

        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", objectIdFieldName);
        writer.WriteStartArray("objectIds");

        var features = _streamingFeatureStore.StreamFeaturesAsync(layerId, query, cancellationToken);
        var idsSinceFlush = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            writer.WriteNumberValue(GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, objectIdFieldName));
            if (++idsSinceFlush >= FlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                idsSinceFlush = 0;
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
        await context.Response.CompleteAsync();
    }

    private static bool ShouldUsePagedQuery(FeatureQuery query)
        => query.Limit is > 0 and < int.MaxValue;

    private static long GetLowerBoundTotalCount(int itemCount, bool hasMoreResults)
        => hasMoreResults ? itemCount + 1L : itemCount;
}
