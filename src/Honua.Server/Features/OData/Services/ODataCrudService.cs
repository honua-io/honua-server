// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData CRUD operations.
/// </summary>
internal sealed class ODataCrudLog;

/// <summary>
/// Service for handling OData CRUD operations (Create, Read, Update, Delete) on features.
/// Provides validation, geometry processing, and error handling for feature operations.
/// </summary>
internal sealed partial class ODataCrudService
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureStore _featureStore;
    private readonly ILogger<ODataCrudLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataCrudService"/> class.
    /// </summary>
    public ODataCrudService(
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<ODataCrudLog> logger)
    {
        _layerCatalog = layerCatalog;
        _featureStore = featureStore;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves a single feature by its ID with proper validation and error handling.
    /// </summary>
    public async Task<ODataCrudResult<ODataFeatureResponse>> GetFeatureAsync(
        int layerId,
        long objectId,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return ODataCrudResult<ODataFeatureResponse>.NotFound($"Layer {layerId} not found");
            }

            // Get the feature
            var feature = await _featureStore.GetAsync(layerId, objectId, cancellationToken);
            if (feature == null)
            {
                return ODataCrudResult<ODataFeatureResponse>.NotFound($"Feature {objectId} not found in layer {layerId}");
            }

            // Feature is guaranteed to be non-null after the null check
            var featureValue = feature.Value;
            var response = new ODataFeatureResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features/$entity",
                ObjectId = featureValue.Id,
                LayerId = layerId,
                Geometry = featureValue.Geometry != null ? Convert.ToBase64String(featureValue.Geometry) : null,
                Attributes = SerializeAttributes(featureValue.Attributes)
            };

            return ODataCrudResult<ODataFeatureResponse>.Success(response, featureValue.Id.ToString());
        }
        catch (Exception ex)
        {
            Log.GetFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<ODataFeatureResponse>.Error("An error occurred processing the OData request");
        }
    }

    /// <summary>
    /// Creates a new feature with validation and geometry processing.
    /// </summary>
    public async Task<ODataCrudResult<ODataFeatureResponse>> CreateFeatureAsync(
        int layerId,
        ODataFeatureRequest request,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return ODataCrudResult<ODataFeatureResponse>.NotFound($"Layer {layerId} not found");
            }

            // Parse and validate geometry if provided
            var geometryResult = ProcessGeometry(request.Geometry);
            if (!geometryResult.IsValid)
            {
                return ODataCrudResult<ODataFeatureResponse>.BadRequest(geometryResult.ErrorMessage!);
            }

            var attributesResult = layer.ValidateAttributes(
                request.Attributes,
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                return ODataCrudResult<ODataFeatureResponse>.BadRequest(attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            var attributes = attributesResult.Value!;

            // Create the feature
            var newFeature = Feature.Create(0, geometryResult.Geometry, attributes);
            var createdFeature = await _featureStore.CreateAsync(layerId, newFeature, cancellationToken);

            var response = new ODataFeatureResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features/$entity",
                ObjectId = createdFeature.Id,
                LayerId = layerId,
                Geometry = createdFeature.Geometry != null ? Convert.ToBase64String(createdFeature.Geometry) : null,
                Attributes = SerializeAttributes(createdFeature.Attributes)
            };

            var locationHeader = $"{baseUrl}/odata/Features({layerId},{createdFeature.Id})";
            return ODataCrudResult<ODataFeatureResponse>.Created(response, createdFeature.Id.ToString(), locationHeader);
        }
        catch (ResourceConflictException ex)
        {
            Log.CreateFeatureFailed(_logger, layerId, ex);
            return ODataCrudResult<ODataFeatureResponse>.Conflict("A conflicting feature already exists.");
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(_logger, layerId, ex);
            return ODataCrudResult<ODataFeatureResponse>.Error("An error occurred creating the feature");
        }
    }

    /// <summary>
    /// Updates an existing feature with partial update support (PATCH semantics).
    /// </summary>
    public async Task<ODataCrudResult<ODataFeatureResponse>> UpdateFeatureAsync(
        int layerId,
        long objectId,
        ODataFeatureRequest request,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return ODataCrudResult<ODataFeatureResponse>.NotFound($"Layer {layerId} not found");
            }

            // Get existing feature to merge with update
            var existingFeature = await _featureStore.GetAsync(layerId, objectId, cancellationToken);
            if (existingFeature == null)
            {
                return ODataCrudResult<ODataFeatureResponse>.NotFound($"Feature {objectId} not found in layer {layerId}");
            }

            // Feature is guaranteed to be non-null after the null check
            var existingFeatureValue = existingFeature.Value;

            // Process geometry - use existing if not provided in update
            byte[]? geometry = existingFeatureValue.Geometry;
            if (!string.IsNullOrWhiteSpace(request.Geometry))
            {
                var geometryResult = ProcessGeometry(request.Geometry);
                if (!geometryResult.IsValid)
                {
                    return ODataCrudResult<ODataFeatureResponse>.BadRequest(geometryResult.ErrorMessage!);
                }
                geometry = geometryResult.Geometry;
            }

            // Merge attributes - new values override existing (PATCH semantics)
            var attributes = new Dictionary<string, object?>(existingFeatureValue.Attributes, StringComparer.OrdinalIgnoreCase);
            if (request.Attributes != null)
            {
                var attributesResult = layer.ValidateAttributes(
                    request.Attributes,
                    ValidationExtensions.AttributeValidationMode.Strict);
                if (!attributesResult.IsValid)
                {
                    return ODataCrudResult<ODataFeatureResponse>.BadRequest(
                        attributesResult.ErrorMessage ?? "Invalid attributes.");
                }

                foreach (var kvp in attributesResult.Value!)
                {
                    attributes[kvp.Key] = kvp.Value;
                }
            }

            // Update the feature
            var updatedFeature = Feature.Create(
                objectId,
                geometry,
                attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
            var result = await _featureStore.UpdateAsync(layerId, updatedFeature, cancellationToken);

            var response = new ODataFeatureResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features/$entity",
                ObjectId = result.Id,
                LayerId = layerId,
                Geometry = result.Geometry != null ? Convert.ToBase64String(result.Geometry) : null,
                Attributes = SerializeAttributes(result.Attributes)
            };

            return ODataCrudResult<ODataFeatureResponse>.Success(response, result.Id.ToString());
        }
        catch (ResourceNotFoundException ex)
        {
            Log.UpdateFeatureNotFound(_logger, layerId, objectId, ex);
            return ODataCrudResult<ODataFeatureResponse>.NotFound($"Feature {objectId} not found in layer {layerId}");
        }
        catch (ResourceConflictException ex)
        {
            Log.UpdateFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<ODataFeatureResponse>.Conflict("The update conflicted with existing data.");
        }
        catch (Exception ex)
        {
            Log.UpdateFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<ODataFeatureResponse>.Error("An error occurred updating the feature");
        }
    }

    /// <summary>
    /// Deletes a feature with proper validation and error handling.
    /// </summary>
    public async Task<ODataCrudResult<object>> DeleteFeatureAsync(
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return ODataCrudResult<object>.NotFound($"Layer {layerId} not found");
            }

            // Delete the feature
            var deleted = await _featureStore.DeleteAsync(layerId, objectId, cancellationToken);
            if (!deleted)
            {
                return ODataCrudResult<object>.NotFound($"Feature {objectId} not found in layer {layerId}");
            }

            return ODataCrudResult<object>.NoContent();
        }
        catch (Exception ex)
        {
            Log.DeleteFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<object>.Error("An error occurred deleting the feature");
        }
    }

    /// <summary>
    /// Processes and validates geometry data from Base64 WKB string.
    /// </summary>
    private static GeometryProcessingResult ProcessGeometry(string? geometryBase64)
    {
        if (string.IsNullOrWhiteSpace(geometryBase64))
        {
            return GeometryProcessingResult.Valid(null);
        }

        try
        {
            var geometry = Convert.FromBase64String(geometryBase64);

            // Validate WKB geometry
            var validationResult = WkbValidation.Validate(geometry);
            if (!validationResult.IsValid)
            {
                return GeometryProcessingResult.Invalid($"Invalid geometry: {validationResult.ErrorMessage}");
            }

            return GeometryProcessingResult.Valid(geometry);
        }
        catch (FormatException)
        {
            return GeometryProcessingResult.Invalid("Geometry must be a valid Base64-encoded WKB string");
        }
    }

    /// <summary>
    /// Serializes feature attributes to JSON string format.
    /// </summary>
    private static string SerializeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = attributes.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        return System.Text.Json.JsonSerializer.Serialize(normalized, ODataJsonContext.Default.DictionaryStringObject);
    }

    /// <summary>
    /// Normalizes values for OData serialization, handling JSON elements and collections.
    /// </summary>
    private static object? NormalizeODataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is System.Text.Json.JsonElement element)
        {
            return ConvertJsonElement(element);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeODataValue(item));
            }

            return list.ToArray();
        }

        return value;
    }

    /// <summary>
    /// Converts JsonElement to appropriate .NET type for serialization.
    /// </summary>
    private static object? ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Logging methods for OData CRUD operations.
    /// </summary>
    private static partial class Log
    {
        /// <summary>
        /// Logs when getting a specific feature fails via OData.
        /// </summary>
        [LoggerMessage(EventId = 3005, Level = LogLevel.Error, Message = "OData get feature failed for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void GetFeatureFailed(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when creating a feature fails via OData.
        /// </summary>
        [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "OData create feature failed for layer {LayerId}.")]
        public static partial void CreateFeatureFailed(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when a feature to update is not found via OData.
        /// </summary>
        [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "OData update feature not found for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void UpdateFeatureNotFound(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when updating a feature fails via OData.
        /// </summary>
        [LoggerMessage(EventId = 3008, Level = LogLevel.Error, Message = "OData update feature failed for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void UpdateFeatureFailed(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when deleting a feature fails via OData.
        /// </summary>
        [LoggerMessage(EventId = 3009, Level = LogLevel.Error, Message = "OData delete feature failed for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void DeleteFeatureFailed(ILogger logger, int layerId, long objectId, Exception exception);
    }
}

/// <summary>
/// Result type for OData CRUD operations with status, data, and metadata.
/// </summary>
/// <typeparam name="T">The type of the result data.</typeparam>
internal sealed record ODataCrudResult<T>
{
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public T? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ETag { get; init; }
    public string? LocationHeader { get; init; }

    private ODataCrudResult() { }

    public static ODataCrudResult<T> Success(T data, string? etag = null)
        => new() { IsSuccess = true, StatusCode = 200, Data = data, ETag = etag };

    public static ODataCrudResult<T> Created(T data, string? etag = null, string? locationHeader = null)
        => new() { IsSuccess = true, StatusCode = 201, Data = data, ETag = etag, LocationHeader = locationHeader };

    public static ODataCrudResult<T> NoContent()
        => new() { IsSuccess = true, StatusCode = 204 };

    public static ODataCrudResult<T> BadRequest(string message)
        => new() { IsSuccess = false, StatusCode = 400, ErrorMessage = message };

    public static ODataCrudResult<T> NotFound(string message)
        => new() { IsSuccess = false, StatusCode = 404, ErrorMessage = message };

    public static ODataCrudResult<T> Conflict(string message)
        => new() { IsSuccess = false, StatusCode = 409, ErrorMessage = message };

    public static ODataCrudResult<T> Error(string message)
        => new() { IsSuccess = false, StatusCode = 500, ErrorMessage = message };
}

/// <summary>
/// Result of geometry processing operations.
/// </summary>
internal sealed record GeometryProcessingResult
{
    public bool IsValid { get; init; }
    public byte[]? Geometry { get; init; }
    public string? ErrorMessage { get; init; }

    private GeometryProcessingResult() { }

    public static GeometryProcessingResult Valid(byte[]? geometry)
        => new() { IsValid = true, Geometry = geometry };

    public static GeometryProcessingResult Invalid(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}
