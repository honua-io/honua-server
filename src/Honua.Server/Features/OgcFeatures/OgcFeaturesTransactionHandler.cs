// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Handler for OGC Features transaction operations and complex multi-step operations.
/// </summary>
internal sealed partial class OgcFeaturesTransactionHandler(
    OgcFeaturesTransactionDependencies dependencies,
    ILogger<OgcFeaturesTransactionHandler> logger)
{
    private readonly IFeatureReader _featureReader = dependencies?.FeatureReader ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureWriter _featureWriter = dependencies.FeatureWriter;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly ILogger<OgcFeaturesTransactionHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles batch feature operations in a single transaction.
    /// </summary>
    public async Task<IResult> HandleBatchOperationAsync(
        string collectionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "batch");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            var crsResult = await OgcRequestCrsResolver.TryResolveInputCrsAsync(
                context.Request,
                layer,
                _crsRegistry,
                cancellationToken);
            if (!crsResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsResult.Error ?? "Invalid Content-Crs header.");
            }

            var inputCrs = crsResult.Definition;

            var (batchRequest, requestError) = await ReadBatchRequestAsync(context, cancellationToken);
            if (batchRequest == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid batch request payload.");
            }

            const int maxBatchOperations = 1000;
            if (batchRequest.Operations.Count > maxBatchOperations)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Batch request exceeds maximum of {maxBatchOperations} operations.");
            }

            var results = new List<BatchOperationResult>();
            var hasErrors = false;

            // Process each operation in the batch
            foreach (var operation in batchRequest.Operations)
            {
                try
                {
                    var result = await ProcessBatchOperationAsync(layerId, layer, operation, inputCrs, cancellationToken);
                    results.Add(result);

                    if (!result.IsSuccess)
                    {
                        hasErrors = true;
                        if (batchRequest.FailFast)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var errorResult = new BatchOperationResult
                    {
                        OperationId = operation.Id,
                        IsSuccess = false,
                        ErrorMessage = "An error occurred processing the operation.",
                        StatusCode = 500
                    };
                    results.Add(errorResult);
                    hasErrors = true;

                    Log.BatchOperationFailed(_logger, collectionId, operation.Id ?? "unknown", ex);

                    if (batchRequest.FailFast)
                    {
                        break;
                    }
                }
            }

            var response = new BatchOperationResponse
            {
                Results = results,
                HasErrors = hasErrors,
                ProcessedCount = results.Count,
                SuccessCount = results.Count(r => r.IsSuccess)
            };

            var statusCode = hasErrors ? 207 : 200; // 207 Multi-Status for partial success
            if (results.Any(result => result.IsSuccess))
            {
                await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
            }
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
            HonuaTelemetry.SetSuccess(activity, response.SuccessCount);
            return Results.Json(response, OgcJsonContext.Default.BatchOperationResponse, statusCode: statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BatchTransactionFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while processing the batch operation.");
        }
    }

    /// <summary>
    /// Handles feature replacement with optimistic concurrency control.
    /// </summary>
    public async Task<IResult> HandleReplaceFeatureAsync(
        string collectionId,
        string featureId,
        string? ifMatch,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "replace");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var (requestFeature, requestError) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            // Check if feature exists and validate ETag if provided
            var existing = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (existing == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = GenerateETag(existing.Value);
                if (!string.Equals(ifMatch.Trim('"'), etag, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }
            }

            var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
                context.Request,
                layer,
                requestFeature,
                _crsRegistry,
                _geometryServices,
                _mutationValidator,
                objectId,
                cancellationToken);
            if (!buildResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    buildResult.ErrorMessage ?? "Invalid feature payload.");
            }

            var inputCrs = buildResult.InputCrs!.Value;
            var feature = buildResult.Feature
                ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

            try
            {
                var updated = await _featureWriter.UpdateAsync(layerId, feature, cancellationToken);
                var newETag = GenerateETag(updated);

                context.Response.Headers.ETag = $"\"{newETag}\"";

                var updateLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    FormattableString.Invariant($"{updated.Id}"),
                    MediaTypes.GeoJson);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(updated, inputCrs.AxisOrder, updateLinks);

                await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
                HonuaTelemetry.SetSuccess(activity);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                Log.ReplaceFeatureConflict(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateConflict(context, ex.Message);
            }
            catch (ResourceNotFoundException)
            {
                Log.ReplaceFeatureNotFound(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException)
            {
                Log.ReplaceFeatureNotFound(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (ArgumentException ex)
            {
                Log.ReplaceFeatureInvalidPayload(_logger, collectionId, featureId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid feature payload.");
            }
            catch (NotSupportedException ex)
            {
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ReplaceFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while replacing the feature.");
        }
    }

    /// <summary>
    /// Handles partial feature updates with merge semantics and optimistic concurrency control.
    /// </summary>
    public async Task<IResult> HandlePatchFeatureAsync(
        string collectionId,
        string featureId,
        string? ifMatch,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "patch");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var existing = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (existing == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = GenerateETag(existing.Value);
                if (!string.Equals(ifMatch.Trim('"'), etag, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }
            }

            var (patchRequest, patchError) = await ReadPatchRequestAsync(context, cancellationToken);
            if (patchRequest == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, patchError ?? "Invalid patch payload.");
            }

            if (patchRequest.FeatureId.HasValue && patchRequest.FeatureId.Value != objectId)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Payload feature ID '{patchRequest.FeatureId.Value}' does not match route feature ID '{featureId}'.");
            }

            var crsResult = await OgcRequestCrsResolver.TryResolveInputCrsAsync(
                context.Request,
                layer,
                _crsRegistry,
                cancellationToken);
            if (!crsResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsResult.Error ?? "Invalid Content-Crs header.");
            }

            var inputCrs = crsResult.Definition;

            byte[]? geometryWkb = existing.Value.Geometry;
            if (patchRequest.HasGeometry)
            {
                if (patchRequest.Geometry == null)
                {
                    geometryWkb = null;
                }
                else
                {
                    var wkbResult = _geometryServices.TryCreateWkbFromGeoJson(
                        patchRequest.Geometry,
                        inputCrs.Srid,
                        inputCrs.AxisOrder);
                    if (!wkbResult.IsSuccess)
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            wkbResult.ErrorMessage ?? "Invalid geometry payload.");
                    }

                    geometryWkb = wkbResult.Wkb;
                }
            }

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(geometryWkb, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Invalid geometry: {geometryValidation.ErrorMessage}");
            }

            var attributesBuilder = existing.Value.Attributes.ToBuilder();
            if (patchRequest.HasProperties)
            {
                if (patchRequest.Properties == null)
                {
                    attributesBuilder.Clear();
                }
                else
                {
                    foreach (var (key, value) in patchRequest.Properties)
                    {
                        if (value == null)
                        {
                            attributesBuilder.Remove(key);
                        }
                        else
                        {
                            attributesBuilder[key] = value;
                        }
                    }
                }
            }

            var attributesResult = _mutationValidator.ValidateAttributes(
                layer,
                attributesBuilder.ToImmutable(),
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            var feature = Feature.Create(
                objectId,
                geometryValidation.Geometry,
                attributesResult.Value!);

            try
            {
                var updated = await _featureWriter.UpdateAsync(layerId, feature, cancellationToken);
                var newETag = GenerateETag(updated);
                context.Response.Headers.ETag = $"\"{newETag}\"";

                var updateLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    FormattableString.Invariant($"{updated.Id}"),
                    MediaTypes.GeoJson);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(updated, inputCrs.AxisOrder, updateLinks);

                await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
                HonuaTelemetry.SetSuccess(activity);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                Log.PatchFeatureConflict(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateConflict(context, ex.Message);
            }
            catch (ResourceNotFoundException)
            {
                Log.PatchFeatureNotFound(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException)
            {
                Log.PatchFeatureNotFound(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (ArgumentException ex)
            {
                Log.PatchFeatureInvalidPayload(_logger, collectionId, featureId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid feature payload.");
            }
            catch (NotSupportedException ex)
            {
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.PatchFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while patching the feature.");
        }
    }

    private async Task<BatchOperationResult> ProcessBatchOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperation operation,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (operation.Type.ToUpperInvariant())
            {
                case "CREATE":
                    return await ProcessCreateOperationAsync(layerId, layer, operation, inputCrs, cancellationToken);
                case "UPDATE":
                    return await ProcessUpdateOperationAsync(layerId, layer, operation, inputCrs, cancellationToken);
                case "DELETE":
                    return await ProcessDeleteOperationAsync(layerId, operation, cancellationToken);
                default:
                    return new BatchOperationResult
                    {
                        OperationId = operation.Id,
                        IsSuccess = false,
                        ErrorMessage = $"Unsupported operation type: {operation.Type}",
                        StatusCode = 400
                    };
            }
        }
        catch (Exception ex)
        {
            Log.BatchOperationFailed(_logger, layerId.ToString(), operation.Id ?? "unknown", ex);
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = "An error occurred processing the operation.",
                StatusCode = 500
            };
        }
    }

    private async Task<BatchOperationResult> ProcessCreateOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperation operation,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        if (operation.Feature == null)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = "Feature data is required for create operation.",
                StatusCode = 400
            };
        }

        var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
            layer,
            operation.Feature,
            inputCrs,
            _geometryServices,
            _mutationValidator,
            objectId: 0,
            cancellationToken);
        if (!buildResult.IsValid)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = buildResult.ErrorMessage ?? "Invalid feature payload.",
                StatusCode = 400
            };
        }

        var feature = buildResult.Feature
            ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");
        var created = await _featureWriter.CreateAsync(layerId, feature, cancellationToken);

        return new BatchOperationResult
        {
            OperationId = operation.Id,
            IsSuccess = true,
            FeatureId = created.Id.ToString(),
            StatusCode = 201
        };
    }

    private async Task<BatchOperationResult> ProcessUpdateOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperation operation,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        if (operation.Feature == null || string.IsNullOrWhiteSpace(operation.FeatureId))
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = "Feature data and feature ID are required for update operation.",
                StatusCode = 400
            };
        }

        if (!long.TryParse(operation.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = $"Invalid feature ID: {operation.FeatureId}",
                StatusCode = 400
            };
        }

        var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
            layer,
            operation.Feature,
            inputCrs,
            _geometryServices,
            _mutationValidator,
            objectId,
            cancellationToken);
        if (!buildResult.IsValid)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = buildResult.ErrorMessage ?? "Invalid feature payload.",
                StatusCode = 400
            };
        }

        var feature = buildResult.Feature
            ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

        try
        {
            var updated = await _featureWriter.UpdateAsync(layerId, feature, cancellationToken);
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = updated.Id.ToString(),
                StatusCode = 200
            };
        }
        catch (ResourceNotFoundException)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = $"Feature '{operation.FeatureId}' not found.",
                StatusCode = 404
            };
        }
    }

    private async Task<BatchOperationResult> ProcessDeleteOperationAsync(
        int layerId,
        BatchOperation operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.FeatureId))
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = "Feature ID is required for delete operation.",
                StatusCode = 400
            };
        }

        if (!long.TryParse(operation.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = $"Invalid feature ID: {operation.FeatureId}",
                StatusCode = 400
            };
        }

        var deleted = await _featureWriter.DeleteAsync(layerId, objectId, cancellationToken);
        if (!deleted)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = false,
                ErrorMessage = $"Feature '{operation.FeatureId}' not found.",
                StatusCode = 404
            };
        }

        return new BatchOperationResult
        {
            OperationId = operation.Id,
            IsSuccess = true,
            StatusCode = 204
        };
    }

    private static async Task<(BatchRequest? Request, string? Error)> ReadBatchRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength == 0)
            {
                return (null, "Request body is required.");
            }

            var request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                OgcJsonContext.Default.BatchRequest,
                cancellationToken);
            return request == null
                ? (null, "Invalid batch request payload.")
                : (request, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static async Task<(PatchRequest? Request, string? Error)> ReadPatchRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength == 0)
            {
                return (null, "Request body is required.");
            }

            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Patch payload must be a JSON object.");
            }

            var root = document.RootElement;
            if (root.TryGetProperty("type", out var typeProperty))
            {
                if (typeProperty.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeProperty.GetString(), "Feature", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, "GeoJSON 'type' must be 'Feature' when provided.");
                }
            }

            long? payloadId = null;
            if (root.TryGetProperty("id", out var idProperty))
            {
                if (!TryReadLong(idProperty, out var parsedId))
                {
                    return (null, "GeoJSON 'id' must be an integer when provided.");
                }

                payloadId = parsedId;
            }

            Dictionary<string, object?>? properties = null;
            var hasProperties = root.TryGetProperty("properties", out var propertiesProperty);
            if (hasProperties)
            {
                if (propertiesProperty.ValueKind == JsonValueKind.Null)
                {
                    properties = null;
                }
                else if (propertiesProperty.ValueKind == JsonValueKind.Object)
                {
                    properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in propertiesProperty.EnumerateObject())
                    {
                        properties[property.Name] = JsonElementConverter.ConvertToObject(property.Value);
                    }
                }
                else
                {
                    return (null, "GeoJSON 'properties' must be an object or null when provided.");
                }
            }

            SimpleGeoJsonGeometry? geometry = null;
            var hasGeometry = root.TryGetProperty("geometry", out var geometryProperty);
            if (hasGeometry)
            {
                if (geometryProperty.ValueKind == JsonValueKind.Null)
                {
                    geometry = null;
                }
                else if (geometryProperty.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        geometry = JsonSerializer.Deserialize(
                            geometryProperty.GetRawText(),
                            OgcJsonContext.Default.SimpleGeoJsonGeometry);
                    }
                    catch (JsonException)
                    {
                        return (null, "Invalid GeoJSON geometry payload.");
                    }

                    if (geometry == null || string.IsNullOrWhiteSpace(geometry.Type))
                    {
                        return (null, "GeoJSON geometry must include a 'type' value.");
                    }
                }
                else
                {
                    return (null, "GeoJSON 'geometry' must be an object or null when provided.");
                }
            }

            return (new PatchRequest(payloadId, hasGeometry, geometry, hasProperties, properties), null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static bool TryReadLong(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private sealed record PatchRequest(
        long? FeatureId,
        bool HasGeometry,
        SimpleGeoJsonGeometry? Geometry,
        bool HasProperties,
        Dictionary<string, object?>? Properties);

    private GeoJsonFeature ToOgcFeature(
        Feature feature,
        AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return feature.ToGeoJsonBase().ToOgcGeoJsonFeature(geometry, links);
    }

    private static string GenerateETag(Feature feature)
    {
        // Generate an ETag based on a hash of the full feature content
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("id", feature.Id);
        if (feature.Geometry != null)
        {
            writer.WriteBase64String("g", feature.Geometry);
        }

        if (feature.Attributes != null)
        {
            writer.WriteStartObject("a");
            foreach (var kvp in feature.Attributes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(kvp.Key);
                WriteAttributeValue(writer, kvp.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()))[..16];
    }

    private static void WriteAttributeValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O"));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O"));
                break;
            case JsonElement je:
                je.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5220, Level = LogLevel.Error, Message = "OGC batch transaction failed for collection {CollectionId}")]
        public static partial void BatchTransactionFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5221, Level = LogLevel.Error, Message = "OGC batch operation failed for collection {CollectionId}, operation {OperationId}")]
        public static partial void BatchOperationFailed(ILogger logger, string collectionId, string operationId, Exception exception);

        [LoggerMessage(EventId = 5222, Level = LogLevel.Error, Message = "OGC replace feature failed for collection {CollectionId}")]
        public static partial void ReplaceFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5223, Level = LogLevel.Error, Message = "OGC patch feature failed for collection {CollectionId}")]
        public static partial void PatchFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5224, Level = LogLevel.Warning, Message = "OGC replace feature conflict for collection {CollectionId}, feature {FeatureId}")]
        public static partial void ReplaceFeatureConflict(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5225, Level = LogLevel.Warning, Message = "OGC replace feature not found for collection {CollectionId}, feature {FeatureId}")]
        public static partial void ReplaceFeatureNotFound(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5226, Level = LogLevel.Warning, Message = "OGC replace feature invalid payload for collection {CollectionId}, feature {FeatureId}: {Reason}")]
        public static partial void ReplaceFeatureInvalidPayload(ILogger logger, string collectionId, string featureId, string reason);

        [LoggerMessage(EventId = 5227, Level = LogLevel.Warning, Message = "OGC patch feature conflict for collection {CollectionId}, feature {FeatureId}")]
        public static partial void PatchFeatureConflict(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5228, Level = LogLevel.Warning, Message = "OGC patch feature not found for collection {CollectionId}, feature {FeatureId}")]
        public static partial void PatchFeatureNotFound(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5229, Level = LogLevel.Warning, Message = "OGC patch feature invalid payload for collection {CollectionId}, feature {FeatureId}: {Reason}")]
        public static partial void PatchFeatureInvalidPayload(ILogger logger, string collectionId, string featureId, string reason);
    }
}
