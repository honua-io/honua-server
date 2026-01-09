// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData streaming query operations for large-scale feature queries.
/// Uses streaming JSON writing to reduce memory pressure for large result sets.
/// </summary>
internal sealed partial class ODataStreamingQueryHandler(
    ODataQueryDependencies dependencies,
    IStreamingFeatureStore streamingFeatureStore,
    ILogger<ODataStreamingQueryHandler> logger)
{
    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IStreamingFeatureStore _streamingFeatureStore = streamingFeatureStore ?? throw new ArgumentNullException(nameof(streamingFeatureStore));
    private readonly ODataValidationService _validationService = dependencies.ValidationService;
    private readonly ODataQuerySearchService _querySearchService = dependencies.QuerySearchService;
    private readonly ILogger<ODataStreamingQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private const int StreamingThreshold = 1000;

    /// <summary>
    /// Handles OData features collection request with streaming for large result sets
    /// </summary>
    public async Task<IResult> HandleGetFeaturesAsync(
        HttpContext context,
        int layerId,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] string? top = null,
        [FromQuery(Name = "$skip")] string? skip = null,
        [FromQuery(Name = "$count")] string? count = null,
        [FromQuery(Name = "$expand")] string? expand = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ValidateAllowedParameters(context, AllowedQueryParameters.Features);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (!ODataParsingUtilities.TryParseOptionalInt(top, "$top", out var topValue, out var parseError))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            }

            if (!ODataParsingUtilities.TryParseOptionalInt(skip, "$skip", out var skipValue, out parseError))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            }

            if (!ODataParsingUtilities.TryParseOptionalBool(count, "$count", out var countValue, out parseError))
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            }

            var paginationResult = _validationService.ValidateAndNormalizePagination(skipValue, topValue);
            if (!paginationResult.IsValid)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption",
                    paginationResult.ErrorMessage ?? "Invalid OData query.");
            }

            var pagination = paginationResult.Value!;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, effectiveToken);
            if (!layerResult.IsValid)
            {
                var errorMessage = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
                var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? 400 : 404;
                var errorCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? "InvalidRequest" : "ResourceNotFound";
                return ODataUtilityService.CreateODataError(context, errorCode, errorMessage, statusCode);
            }

            var layer = layerResult.Resource!;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            // Build feature query using query service
            var featureQuery = _querySearchService.BuildFeatureQuery(
                filter, orderby, pagination.Limit,
                pagination.Offset, layer, out var queryError);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            // Determine if we should use streaming
            bool useStreaming = pagination.Limit > StreamingThreshold && string.IsNullOrWhiteSpace(expand);

            if (!useStreaming)
            {
                // For small result sets or when $expand is used, delegate to non-streaming handler
                var queryHandler = context.RequestServices.GetRequiredService<ODataQueryHandler>();
                return await queryHandler.HandleGetFeaturesNonStreamingAsync(
                    context, layerId, filter, select, orderby, topValue, skipValue, countValue, expand, cancellationToken);
            }

            // Get total count if requested
            long? totalCount = null;
            if (countValue == true)
            {
                totalCount = await _featureReader.CountAsync(layerId, featureQuery, effectiveToken);
            }

            // Set up streaming response
            context.Response.ContentType = ODataUtilityService.GetODataContentType();
            context.Response.Headers["Transfer-Encoding"] = "chunked";
            ODataUtilityService.SetODataHeaders(context);

            // Stream the OData response
            await StreamODataFeaturesAsync(
                (IAsyncEnumerable<Feature>)_streamingFeatureStore.StreamFeaturesAsync(layerId, featureQuery, cancellationToken),
                context,
                layerId,
                select,
                totalCount,
                cancellationToken);

            return Results.Empty;
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidFeaturesQuery(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.FeaturesQueryFailed(_logger, layerId, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Streams OData features response to reduce memory pressure
    /// </summary>
    private static async Task StreamODataFeaturesAsync(
        IAsyncEnumerable<Feature> features,
        HttpContext context,
        int layerId,
        string? select,
        long? totalCount,
        CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(context.Response.Body, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        // Start OData response
        writer.WriteStartObject();

        if (totalCount.HasValue)
        {
            writer.WriteNumber("@odata.count", totalCount.Value);
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        writer.WriteString("@odata.context", $"{baseUrl}/$metadata#Features");

        // Start value array
        writer.WriteStartArray("value");

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await WriteODataFeatureAsync(writer, feature, layerId, select, cancellationToken);

            // Flush periodically for better streaming
            await writer.FlushAsync(cancellationToken);
        }

        // End value array
        writer.WriteEndArray();

        // End OData response
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a single feature in OData format
    /// </summary>
    private static async Task WriteODataFeatureAsync(
        Utf8JsonWriter writer,
        Feature feature,
        int layerId,
        string? select,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();

        // Core OData feature properties
        writer.WriteNumber("ObjectId", feature.Id);
        writer.WriteNumber("LayerId", layerId);

        if (feature.Geometry != null)
        {
            writer.WriteString("Geometry", Convert.ToBase64String(feature.Geometry));
        }
        else
        {
            writer.WriteNull("Geometry");
        }

        // Write attributes
        if (feature.Attributes != null)
        {
            writer.WriteStartObject("Attributes");
            foreach (var kvp in feature.Attributes)
            {
                await WriteODataJsonValueAsync(writer, kvp.Key, kvp.Value, cancellationToken);
            }
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("Attributes");
        }

        writer.WriteEndObject(); // End feature

        // Allow for cancellation during processing
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Writes OData JSON values with proper type handling
    /// </summary>
    private static async Task WriteODataJsonValueAsync(
        Utf8JsonWriter writer,
        string propertyName,
        object? value,
        CancellationToken cancellationToken)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case string s:
                writer.WriteString(propertyName, s);
                break;
            case int i:
                writer.WriteNumber(propertyName, i);
                break;
            case long l:
                writer.WriteNumber(propertyName, l);
                break;
            case double d:
                writer.WriteNumber(propertyName, d);
                break;
            case float f:
                writer.WriteNumber(propertyName, f);
                break;
            case decimal dec:
                writer.WriteNumber(propertyName, dec);
                break;
            case bool b:
                writer.WriteBoolean(propertyName, b);
                break;
            case DateTime dt:
                writer.WriteString(propertyName, dt.ToString("O")); // ISO 8601 format
                break;
            case DateTimeOffset dto:
                writer.WriteString(propertyName, dto.ToString("O")); // ISO 8601 format
                break;
            default:
                // For complex objects, serialize using OData JSON context
                writer.WritePropertyName(propertyName);
                JsonSerializer.Serialize(writer, value, ODataJsonContext.Default.Object);
                break;
        }

        // Allow for cancellation during long attribute writing
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private IResult? ValidateAllowedParameters(
        HttpContext context,
        IReadOnlySet<string> allowedParameters)
    {
        var validationResult = _validationService.ValidateAllowedParameters(context.Request.Query.Keys.ToArray(), allowedParameters);
        if (!validationResult.IsValid)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                validationResult.ErrorMessage ?? "Invalid query parameter.");
        }

        return null;
    }


    /// <summary>
    /// Logging methods for OData streaming query operations.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Invalid OData streaming features query for layer {LayerId}.")]
        public static partial void InvalidFeaturesQuery(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "OData streaming features query failed for layer {LayerId}.")]
        public static partial void FeaturesQueryFailed(ILogger logger, int layerId, Exception exception);
    }
}
