// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Npgsql;

namespace Honua.Server.Features.FeatureServer.Services;

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
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException($"Invalid query syntax: {ex.Message}");
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException($"Query execution failed: {ex.Message}");
        }
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
            throw new InvalidOperationException($"Invalid query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid query format: {ex.Message}");
        }
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException($"Invalid query syntax: {ex.Message}");
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException($"Query execution failed: {ex.Message}");
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
            throw new InvalidOperationException($"Invalid query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid query format: {ex.Message}");
        }
        catch (PostgresException ex) when (QueryExceptionClassifier.IsInvalidQuerySyntax(ex))
        {
            throw new InvalidOperationException($"Invalid query syntax: {ex.Message}");
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException($"Query execution failed: {ex.Message}");
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
        EnableChunkedEncodingIfHttp1(context);

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
        var idsSinceFlush = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            writer.WriteNumberValue(feature.Id);
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

    private static void EnableChunkedEncodingIfHttp1(HttpContext context)
    {
        if (context.Request.Protocol.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.ContentLength = null;
            context.Response.Headers.TransferEncoding = "chunked";
        }
    }
}
