// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;

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
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context, collectionId, scope: AccessScope.Write, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                context,
                layerId,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
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
                await InvalidateCacheAsync(context, layerId, cancellationToken);
            }
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
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
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context, collectionId, scope: AccessScope.Write, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                context,
                layerId,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
            }

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

                var updateLinks = BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    FormattableString.Invariant($"{updated.Id}"),
                    MediaTypes.GeoJson);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(updated, inputCrs.AxisOrder, updateLinks);

                await InvalidateCacheAsync(context, layerId, cancellationToken);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                return StandardErrorHelpers.CreateConflict(context, ex.Message);
            }
            catch (ResourceNotFoundException)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (ArgumentException)
            {
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

    private static async Task InvalidateCacheAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken);
        }
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

    private GeoJsonFeature ToOgcFeature(
        Feature feature,
        AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return feature.ToGeoJsonBase().ToOgcGeoJsonFeature(geometry, links);
    }

    private static ImmutableArray<Link> BuildFeatureLinks(
        HttpRequest request,
        string collectionId,
        string featureId,
        string outputFormat)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{featureId}";

        return new List<Link>
        {
            Link.Create(
                href: basePath,
                rel: RelationTypes.Self,
                type: outputFormat,
                title: "Feature")
        }.ToImmutableArray();
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
                JsonSerializer.Serialize(writer, kvp.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()))[..16];
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5220, Level = LogLevel.Error, Message = "OGC batch transaction failed for collection {CollectionId}")]
        public static partial void BatchTransactionFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5221, Level = LogLevel.Error, Message = "OGC batch operation failed for collection {CollectionId}, operation {OperationId}")]
        public static partial void BatchOperationFailed(ILogger logger, string collectionId, string operationId, Exception exception);

        [LoggerMessage(EventId = 5222, Level = LogLevel.Error, Message = "OGC replace feature failed for collection {CollectionId}")]
        public static partial void ReplaceFeatureFailed(ILogger logger, string collectionId, Exception exception);
    }
}
