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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var objectIdSample = BuildObjectIdSample(queryParams.ObjectIds);
                FeatureServerLog.RelatedRecordsQueryRequested(
                    _logger,
                    serviceId,
                    layerId,
                    queryParams.ObjectIds.Length,
                    objectIdSample,
                    queryParams.RelationshipId);
            }

            var resourceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
                _resourceValidator,
                serviceId,
                layerId,
                httpContext,
                _logger,
                cancellationToken);
            if (!resourceValidationResult.IsValid)
            {
                return resourceValidationResult.ErrorResult!;
            }

            ServiceDefinition service = resourceValidationResult.Service!;
            LayerDefinition layer = resourceValidationResult.Layer!;
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
            RelatedRecordsValidationResult limitValidationResult = _queryServices.ValidateRelatedRecordsLimits(queryParams);
            if (!limitValidationResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Query parameters exceed configured limits",
                    [limitValidationResult.ErrorMessage!]);
            }

            QueryRelatedRecordsParameters validatedParams = limitValidationResult.ValidatedParameters!;
            if (!FeatureServerEndpoints.TryValidateOutputFormat(
                validatedParams.F,
                FeatureServerEndpoints.JsonOnlyFormats,
                out _,
                out var formatError))
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Invalid query parameters",
                    [formatError ?? "Output format is not supported."]);
            }

            if (!TryValidateUnsupportedRelatedRecordsParameters(validatedParams, out var unsupportedError))
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Unsupported query parameters",
                    [unsupportedError!]);
            }

            // Get related layer information
            var relatedLayer = service.Layers.FirstOrDefault(l => l.Id == relationship.RelatedLayerId);
            if (relatedLayer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, relationship.RelatedLayerId);
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Related layer {relationship.RelatedLayerId} not found in service '{serviceId}'");
            }

            var objectIds = queryParams.ObjectIds;
            var outputSrid = await _queryServices.ResolveSridAsync(validatedParams.OutSr, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.OutSr) && !outputSrid.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Invalid output spatial reference",
                    [$"Unsupported outSR value: {validatedParams.OutSr}"]);
            }

            outputSrid ??= relatedLayer.SpatialReference.ToSrid();

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
            var objectIdFieldName = relatedLayer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
            RelatedRecordGroup[] relatedRecordGroups = _relatedRecordsService.GroupRelatedRecords(
                result,
                objectIds,
                relationship,
                objectIdFieldName,
                validatedParams.ReturnGeometry,
                outputSrid,
                validatedParams.ReturnZ,
                validatedParams.ReturnM,
                validatedParams.GeometryPrecision,
                validatedParams.MaxAllowableOffset,
                relatedQuery.OutFields);

            // Build response
            var response = new QueryRelatedRecordsResponse
            {
                RelatedRecordGroups = relatedRecordGroups
            };

            var totalRelatedRecords = relatedRecordGroups.Sum(g => g.RelatedRecords?.Features?.Length ?? 0);
            FeatureServerLog.RelatedRecordsQueryCompleted(_logger, serviceId, layerId,
                totalRelatedRecords, relatedRecordGroups.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Honua.Core.Exceptions.ResourceNotFoundException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            return StandardErrorHelpers.CreateNotFound(_httpContextAccessor.HttpContext!, ex.Message);
        }
        catch (Honua.Core.Exceptions.ValidationException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            return StandardErrorHelpers.CreateBadRequest(_httpContextAccessor.HttpContext!, ex.Message);
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

    private static bool TryValidateUnsupportedRelatedRecordsParameters(
        QueryRelatedRecordsParameters queryParams,
        out string? errorMessage)
    {
        var unsupported = new List<string>();

        if (queryParams.ReturnTrueCurves)
        {
            unsupported.Add("returnTrueCurves");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.GdbVersion))
        {
            unsupported.Add("gdbVersion");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.SqlFormat))
        {
            unsupported.Add("sqlFormat");
        }

        if (queryParams.HistoricMoment.HasValue)
        {
            unsupported.Add("historicMoment");
        }

        if (unsupported.Count == 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = $"Unsupported query parameters: {string.Join(", ", unsupported)}.";
        return false;
    }

    private static string BuildObjectIdSample(long[] objectIds)
    {
        const int maxSampleCount = 10;
        if (objectIds.Length == 0)
        {
            return string.Empty;
        }

        var sample = string.Join(",", objectIds.Take(maxSampleCount));
        return objectIds.Length > maxSampleCount
            ? $"{sample},..."
            : sample;
    }
}
