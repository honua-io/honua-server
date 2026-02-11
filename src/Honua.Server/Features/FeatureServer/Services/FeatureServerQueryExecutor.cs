// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Executes FeatureServer queries and handles streaming responses.
/// </summary>
internal sealed class FeatureServerQueryExecutor
{
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
            return await _featureReader.QueryAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid query format: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("syntax") || ex.Message.Contains("SQL") || ex.Message.Contains("parse"))
        {
            throw new InvalidOperationException($"Invalid query syntax: {ex.Message}");
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
        var totalCount = await _featureReader.CountAsync(layerId, query, cancellationToken);
        var offset = query.Offset ?? 0;
        var hasMoreResults = query.Limit.HasValue && totalCount > offset + query.Limit.Value;

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
        EnableChunkedEncodingIfHttp1(context);

        var features = _streamingFeatureStore.StreamFeaturesAsync(layerId, query, cancellationToken);

        if (string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase))
        {
            await _streamingFormatter.StreamAsGeoJsonAsync(
                features,
                layer,
                queryParams.ReturnGeometry,
                queryParams.ReturnZ,
                queryParams.ReturnM,
                queryParams.GeometryPrecision,
                queryParams.MaxAllowableOffset,
                outFields,
                hasMoreResults,
                context.Response.BodyWriter,
                cancellationToken);
        }
        else
        {
            await _streamingFormatter.StreamAsGeoServicesJsonAsync(
                features,
                layer,
                queryParams.ReturnGeometry,
                outputSrid,
                queryParams.ReturnZ,
                queryParams.ReturnM,
                queryParams.GeometryPrecision,
                queryParams.MaxAllowableOffset,
                outFields,
                hasMoreResults,
                context.Response.BodyWriter,
                cancellationToken);
        }

        await context.Response.CompleteAsync();
    }

    public async Task StreamIdsAsync(
        int layerId,
        FeatureQuery query,
        string objectIdFieldName,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status200OK;
        EnableChunkedEncodingIfHttp1(context);

        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", objectIdFieldName);
        writer.WriteStartArray("objectIds");

        var features = _streamingFeatureStore.StreamFeaturesAsync(layerId, query, cancellationToken);
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            writer.WriteNumberValue(feature.Id);
            await writer.FlushAsync(cancellationToken);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
        await context.Response.CompleteAsync();
    }

    private static void EnableChunkedEncodingIfHttp1(HttpContext context)
    {
        if (context.Request.Protocol.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.ContentLength = null;
            context.Response.Headers.TransferEncoding = "chunked";
        }
    }
}
