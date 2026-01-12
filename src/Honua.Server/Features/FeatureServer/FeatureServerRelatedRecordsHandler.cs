// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer related records operations.
/// </summary>
internal sealed class FeatureServerRelatedRecordsHandler(
    FeatureServerRelatedRecordsDependencies dependencies,
    ILogger<FeatureServerRelatedRecordsHandler> logger)
{
    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureServerQueryServices _queryServices = dependencies.QueryServices;
    private readonly IRelatedRecordsService _relatedRecordsService = dependencies.RelatedRecordsService;
    private readonly IFilterExpressionService _filterExpressionService = dependencies.FilterExpressionService;
    private readonly IHttpContextAccessor _httpContextAccessor = dependencies.HttpContextAccessor;
    private readonly ILogger<FeatureServerRelatedRecordsHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Executes a query for related records with proper validation and formatting.
    /// </summary>
    public async Task<IResult> HandleQueryRelatedRecordsAsync(
        string serviceId,
        int layerId,
        QueryRelatedRecordsParameters queryParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext!;
            string objectIdsString = string.Join(",", queryParams.ObjectIds);
            FeatureServerLog.RelatedRecordsQueryRequested(_logger, serviceId, layerId, objectIdsString, queryParams.RelationshipId);

            var resourceResult = await _resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
            if (!resourceResult.IsValid)
            {
                var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";

                if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(httpContext, errorMessage);
                }

                if (errorMessage.StartsWith("Service", StringComparison.OrdinalIgnoreCase))
                {
                    FeatureServerLog.ServiceNotFound(_logger, serviceId);
                }
                else if (errorMessage.StartsWith("Layer", StringComparison.OrdinalIgnoreCase))
                {
                    FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                }

                return StandardErrorHelpers.CreateNotFound(httpContext, errorMessage);
            }

            ServiceDefinition service = resourceResult.Resource!.Service;
            LayerDefinition layer = resourceResult.Resource.Layer;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(httpContext, layer, service);
            if (accessError != null)
            {
                return accessError;
            }

            // Validate required parameters (these should already be validated by parameter parsing)
            if (queryParams.ObjectIds.Length == 0)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Invalid query parameters",
                    ["objectIds parameter is required"]);
            }

            var relationship = layer.LayerRelationships.FirstOrDefault(r => r.RelationshipId == queryParams.RelationshipId);
            if (relationship.RelationshipId == 0)
            {
                FeatureServerLog.RelationshipNotFound(_logger, layerId, queryParams.RelationshipId);
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Relationship {queryParams.RelationshipId} not found for layer {layerId}");
            }

            // Apply limits enforcement
            RelatedRecordsValidationResult validationResult = _queryServices.ValidateRelatedRecordsLimits(queryParams);
            if (!validationResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Query parameters exceed configured limits",
                    [validationResult.ErrorMessage!]);
            }

            QueryRelatedRecordsParameters validatedParams = validationResult.ValidatedParameters!;

            // Get related layer information
            var relatedLayer = service.Layers.FirstOrDefault(l => l.Id == relationship.RelatedLayerId);
            if (relatedLayer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, relationship.RelatedLayerId);
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Related layer {relationship.RelatedLayerId} not found in service '{serviceId}'");
            }

            var objectIds = queryParams.ObjectIds;

            SqlFragment? sqlFilter = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Where))
            {
                var parseResult = _filterExpressionService.Parse(FilterLanguage.ArcGisSql, validatedParams.Where);
                if (!parseResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(httpContext,
                        ErrorMessages.Validation.InvalidParameter,
                        [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                if (parseResult.Expression != null)
                {
                    var translationResult = _filterExpressionService.Translate(parseResult.Expression, relatedLayer);
                    if (!translationResult.IsSuccess)
                    {
                        return StandardErrorHelpers.CreateBadRequest(httpContext,
                            ErrorMessages.Validation.InvalidParameter,
                            [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                    }

                    sqlFilter = translationResult.SqlFilter;
                }
            }

            // Build related query from validated parameters
            RelatedQuery relatedQuery = _relatedRecordsService.BuildRelatedQuery(
                validatedParams,
                objectIds,
                relationship,
                sqlFilter);

            // Execute related query
            QueryResult<Feature> result = await _relatedRecordsService.ExecuteRelatedQueryAsync(layerId, relatedQuery, cancellationToken);

            // Group results by origin object ID
            RelatedRecordGroup[] relatedRecordGroups = _relatedRecordsService.GroupRelatedRecords(
                result,
                objectIds,
                relationship,
                validatedParams.ReturnGeometry,
                relatedLayer.SpatialReference.ToSrid(),
                relatedQuery.OutFields);

            // Build response
            var response = new QueryRelatedRecordsResponse
            {
                RelatedRecordGroups = relatedRecordGroups
            };

            FeatureServerLog.RelatedRecordsQueryCompleted(_logger, serviceId, layerId,
                relatedRecordGroups.Sum(g => g.RelatedRecords?.Features?.Length ?? 0), relatedRecordGroups.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            // Return safe error message without leaking exception details
            var httpContext = _httpContextAccessor.HttpContext!;
            return StandardErrorHelpers.CreateBadRequest(httpContext, "Invalid query parameters");
        }
        catch (Exception ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            var httpContext = _httpContextAccessor.HttpContext!;
            return StandardErrorHelpers.CreateInternalServerError(httpContext, "Related records query execution failed");
        }
    }
}
