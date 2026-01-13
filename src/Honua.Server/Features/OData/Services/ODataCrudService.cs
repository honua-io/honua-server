// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData CRUD operations.
/// </summary>
internal sealed class ODataCrudLog;

internal sealed record ODataCrudDependencies(
    IResourceValidator ResourceValidator,
    IFeatureReader FeatureReader,
    IFeatureWriter FeatureWriter,
    IGeometryService GeometryService,
    ICrsRegistry CrsRegistry,
    IETagService ETagService,
    FeatureMutationValidator MutationValidator);

/// <summary>
/// Service for handling OData CRUD operations (Create, Read, Update, Delete) on features.
/// Provides validation, geometry processing, and error handling for feature operations.
/// </summary>
internal sealed partial class ODataCrudService
{
    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IGeometryService _geometryService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly IETagService _etagService;
    private readonly FeatureMutationValidator _mutationValidator;
    private readonly ILogger<ODataCrudLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataCrudService"/> class.
    /// </summary>
    public ODataCrudService(
        ODataCrudDependencies dependencies,
        ILogger<ODataCrudLog> logger)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        _resourceValidator = dependencies.ResourceValidator ?? throw new ArgumentNullException(nameof(dependencies.ResourceValidator));
        _featureReader = dependencies.FeatureReader ?? throw new ArgumentNullException(nameof(dependencies.FeatureReader));
        _featureWriter = dependencies.FeatureWriter ?? throw new ArgumentNullException(nameof(dependencies.FeatureWriter));
        _geometryService = dependencies.GeometryService ?? throw new ArgumentNullException(nameof(dependencies.GeometryService));
        _crsRegistry = dependencies.CrsRegistry ?? throw new ArgumentNullException(nameof(dependencies.CrsRegistry));
        _etagService = dependencies.ETagService ?? throw new ArgumentNullException(nameof(dependencies.ETagService));
        _mutationValidator = dependencies.MutationValidator ?? throw new ArgumentNullException(nameof(dependencies.MutationValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Retrieves a single feature by its ID with proper validation and error handling.
    /// </summary>
    public async Task<ODataCrudResult<Dictionary<string, object?>>> GetFeatureAsync(
        int layerId,
        long objectId,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
            if (!layerResult.IsValid)
            {
                return CreateLayerErrorResult<Dictionary<string, object?>>(layerResult, layerId);
            }

            // Get the feature
            var feature = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (feature == null)
            {
                return ODataCrudResult<Dictionary<string, object?>>.NotFound($"Feature {objectId} not found in layer {layerId}");
            }

            // Feature is guaranteed to be non-null after the null check
            var featureValue = feature.Value;
            var layer = layerResult.Resource!;
            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                layer.SpatialReference.ToSrid(),
                cancellationToken);
            var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
                _geometryService,
                featureValue.Geometry,
                layer.SpatialReference.ToSrid(),
                axisOrder);
            var attributes = ODataAttributeSerializer.Serialize(featureValue.Attributes);
            var response = ODataUtilityService.BuildFeaturePayload(layerId, featureValue, geometry, attributes);
            var etag = ComputeFeatureEtag(layerId, featureValue, geometry, attributes);

            return ODataCrudResult<Dictionary<string, object?>>.Success(response, etag);
        }
        catch (Exception ex)
        {
            Log.GetFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.Error("An error occurred processing the OData request");
        }
    }

    /// <summary>
    /// Creates a new feature with validation and geometry processing.
    /// </summary>
    public async Task<ODataCrudResult<Dictionary<string, object?>>> CreateFeatureAsync(
        int layerId,
        ParsedFeaturePayload payload,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
            if (!layerResult.IsValid)
            {
                return CreateLayerErrorResult<Dictionary<string, object?>>(layerResult, layerId);
            }

            var layer = layerResult.Resource!;

            // Parse and validate geometry if provided
            var geometryResult = await ProcessGeometryAsync(payload.Geometry, layer.SpatialReference.ToSrid(), cancellationToken);
            if (!geometryResult.IsValid)
            {
                return ODataCrudResult<Dictionary<string, object?>>.BadRequest(geometryResult.ErrorMessage!);
            }

            var attributesResult = _mutationValidator.ValidateAttributes(
                layer,
                payload.Attributes,
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                return ODataCrudResult<Dictionary<string, object?>>.BadRequest(attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            var validatedAttributes = attributesResult.Value!;

            // Create the feature
            var newFeature = Feature.Create(0, geometryResult.Geometry, validatedAttributes);
            var createdFeature = await _featureWriter.CreateAsync(layerId, newFeature, cancellationToken);

            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                layer.SpatialReference.ToSrid(),
                cancellationToken);
            var responseGeometry = ODataGeometryConverter.ConvertWkbToGeometry(
                _geometryService,
                createdFeature.Geometry,
                layer.SpatialReference.ToSrid(),
                axisOrder);
            var serializedAttributes = ODataAttributeSerializer.Serialize(createdFeature.Attributes);
            var response = ODataUtilityService.BuildFeaturePayload(layerId, createdFeature, responseGeometry, serializedAttributes);
            var etag = ComputeFeatureEtag(layerId, createdFeature, responseGeometry, serializedAttributes);

            var locationHeader = $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={createdFeature.Id})";
            return ODataCrudResult<Dictionary<string, object?>>.Created(response, etag, locationHeader);
        }
        catch (ResourceConflictException ex)
        {
            Log.CreateFeatureFailed(_logger, layerId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.Conflict("A conflicting feature already exists.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidOperationException or NotSupportedException)
        {
            Log.CreateFeatureFailed(_logger, layerId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(_logger, layerId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.Error("An error occurred creating the feature");
        }
    }

    /// <summary>
    /// Updates an existing feature with partial update support (PATCH semantics).
    /// </summary>
    public async Task<ODataCrudResult<Dictionary<string, object?>>> UpdateFeatureAsync(
        int layerId,
        long objectId,
        ParsedFeaturePayload payload,
        string baseUrl,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
            if (!layerResult.IsValid)
            {
                return CreateLayerErrorResult<Dictionary<string, object?>>(layerResult, layerId);
            }

            var layer = layerResult.Resource!;

            // Get existing feature to merge with update
            var existingFeature = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (existingFeature == null)
            {
                return ODataCrudResult<Dictionary<string, object?>>.NotFound($"Feature {objectId} not found in layer {layerId}");
            }

            // Feature is guaranteed to be non-null after the null check
            var existingFeatureValue = existingFeature.Value;

            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                layer.SpatialReference.ToSrid(),
                cancellationToken);
            var currentGeometry = ODataGeometryConverter.ConvertWkbToGeometry(
                _geometryService,
                existingFeatureValue.Geometry,
                layer.SpatialReference.ToSrid(),
                axisOrder);
            var currentAttributes = ODataAttributeSerializer.Serialize(existingFeatureValue.Attributes);
            var currentEtag = ComputeFeatureEtag(layerId, existingFeatureValue, currentGeometry, currentAttributes);

            if (!string.IsNullOrWhiteSpace(ifMatch) &&
                !_etagService.MatchesPrecondition(ifMatch, currentEtag))
            {
                return ODataCrudResult<Dictionary<string, object?>>.PreconditionFailed(
                    "ETag does not match the current resource.");
            }

            if (!string.IsNullOrWhiteSpace(ifNoneMatch) &&
                !_etagService.IsModified(ifNoneMatch, currentEtag))
            {
                return ODataCrudResult<Dictionary<string, object?>>.PreconditionFailed(
                    "Resource has not changed.");
            }

            // Process geometry - use existing if not provided in update
            byte[]? geometryBytes = existingFeatureValue.Geometry;
            if (payload.Geometry != null)
            {
                var geometryResult = await ProcessGeometryAsync(payload.Geometry, layer.SpatialReference.ToSrid(), cancellationToken);
                if (!geometryResult.IsValid)
                {
                    return ODataCrudResult<Dictionary<string, object?>>.BadRequest(geometryResult.ErrorMessage!);
                }
                geometryBytes = geometryResult.Geometry;
            }

            // Merge attributes - new values override existing (PATCH semantics)
            var mergedAttributes = new Dictionary<string, object?>(existingFeatureValue.Attributes, StringComparer.OrdinalIgnoreCase);
            if (payload.Attributes.Count > 0)
            {
                var attributesResult = _mutationValidator.ValidateAttributes(
                    layer,
                    payload.Attributes,
                    ValidationExtensions.AttributeValidationMode.Strict);
                if (!attributesResult.IsValid)
                {
                    return ODataCrudResult<Dictionary<string, object?>>.BadRequest(
                        attributesResult.ErrorMessage ?? "Invalid attributes.");
                }

                foreach (var kvp in attributesResult.Value!)
                {
                    mergedAttributes[kvp.Key] = kvp.Value;
                }
            }

            // Update the feature
            var updatedFeature = Feature.Create(
                objectId,
                geometryBytes,
                mergedAttributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
            var result = await _featureWriter.UpdateAsync(layerId, updatedFeature, cancellationToken);

            var responseGeometry = ODataGeometryConverter.ConvertWkbToGeometry(
                _geometryService,
                result.Geometry,
                layer.SpatialReference.ToSrid(),
                axisOrder);
            var serializedAttributes = ODataAttributeSerializer.Serialize(result.Attributes);
            var response = ODataUtilityService.BuildFeaturePayload(layerId, result, responseGeometry, serializedAttributes);
            var etag = ComputeFeatureEtag(layerId, result, responseGeometry, serializedAttributes);

            return ODataCrudResult<Dictionary<string, object?>>.Success(response, etag);
        }
        catch (ResourceNotFoundException ex)
        {
            Log.UpdateFeatureNotFound(_logger, layerId, objectId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.NotFound($"Feature {objectId} not found in layer {layerId}");
        }
        catch (ResourceConflictException ex)
        {
            Log.UpdateFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.Conflict("The update conflicted with existing data.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidOperationException or NotSupportedException)
        {
            Log.UpdateFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            Log.UpdateFeatureFailed(_logger, layerId, objectId, ex);
            return ODataCrudResult<Dictionary<string, object?>>.Error("An error occurred updating the feature");
        }
    }

    /// <summary>
    /// Deletes a feature with proper validation and error handling.
    /// </summary>
    public async Task<ODataCrudResult<object>> DeleteFeatureAsync(
        int layerId,
        long objectId,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
            if (!layerResult.IsValid)
            {
                return CreateLayerErrorResult<object>(layerResult, layerId);
            }

            var existingFeature = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (!existingFeature.HasValue)
            {
                return ODataCrudResult<object>.NotFound($"Feature {objectId} not found in layer {layerId}");
            }

            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                layerResult.Resource!.SpatialReference.ToSrid(),
                cancellationToken);
            var currentGeometry = ODataGeometryConverter.ConvertWkbToGeometry(
                _geometryService,
                existingFeature.Value.Geometry,
                layerResult.Resource!.SpatialReference.ToSrid(),
                axisOrder);
            var currentAttributes = ODataAttributeSerializer.Serialize(existingFeature.Value.Attributes);
            var currentEtag = ComputeFeatureEtag(layerId, existingFeature.Value, currentGeometry, currentAttributes);

            if (!string.IsNullOrWhiteSpace(ifMatch) &&
                !_etagService.MatchesPrecondition(ifMatch, currentEtag))
            {
                return ODataCrudResult<object>.PreconditionFailed("ETag does not match the current resource.");
            }

            if (!string.IsNullOrWhiteSpace(ifNoneMatch) &&
                !_etagService.IsModified(ifNoneMatch, currentEtag))
            {
                return ODataCrudResult<object>.PreconditionFailed("Resource has not changed.");
            }

            // Delete the feature
            var deleted = await _featureWriter.DeleteAsync(layerId, objectId, cancellationToken);
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

    private static ODataCrudResult<T> CreateLayerErrorResult<T>(
        ResourceValidationResult<LayerDefinition> result,
        int layerId)
    {
        var message = result.ErrorMessage ?? $"Layer {layerId} not found";
        return result.ErrorCode == ResourceValidationError.InvalidIdentifier
            ? ODataCrudResult<T>.BadRequest(message)
            : ODataCrudResult<T>.NotFound(message);
    }

    /// <summary>
    /// Processes and validates geometry data from OData spatial payload.
    /// </summary>
    private async Task<GeometryProcessingResult> ProcessGeometryAsync(
        ODataSpatialGeometry? geometry,
        int defaultSrid,
        CancellationToken cancellationToken)
    {
        var crsResolution = await ODataCrsUtilities.TryResolveGeometryCrsAsync(
            _crsRegistry,
            geometry,
            defaultSrid,
            cancellationToken);
        if (!crsResolution.IsValid)
        {
            return GeometryProcessingResult.Invalid(crsResolution.ErrorMessage ?? "Unsupported geometry CRS.");
        }

        var conversion = ODataGeometryConverter.ConvertGeometryToWkb(
            _geometryService,
            geometry,
            defaultSrid,
            crsResolution.Definition);
        if (!conversion.IsSuccess)
        {
            return GeometryProcessingResult.Invalid(conversion.ErrorMessage ?? "Invalid geometry.");
        }

        if (conversion.Wkb == null)
        {
            return GeometryProcessingResult.Valid(null);
        }

        var geometryValidation = await _mutationValidator.ValidateGeometryAsync(conversion.Wkb, cancellationToken);
        if (!geometryValidation.IsValid)
        {
            return GeometryProcessingResult.Invalid($"Invalid geometry: {geometryValidation.ErrorMessage}");
        }

        return GeometryProcessingResult.Valid(geometryValidation.Geometry);
    }

    private string ComputeFeatureEtag(
        int layerId,
        Feature feature,
        ODataSpatialGeometry? geometry,
        IReadOnlyDictionary<string, object?> attributes)
    {
        var payload = ODataUtilityService.BuildFeaturePayload(layerId, feature, geometry, attributes);
        var canonical = ODataUtilityService.NormalizeForEtag(payload);
        var json = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return _etagService.ComputeETag(json);
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

    public static ODataCrudResult<T> PreconditionFailed(string message)
        => new() { IsSuccess = false, StatusCode = 412, ErrorMessage = message };

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
