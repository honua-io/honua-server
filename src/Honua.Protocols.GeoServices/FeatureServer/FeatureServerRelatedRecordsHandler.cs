// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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

            var resourceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
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

            var service = resourceValidationResult.Service!;
            var publication = resourceValidationResult.Publication!;
            var resource = resourceValidationResult.Resource!;
            var accessError = AccessPolicyHelpers.RequireResourceAccess(httpContext, resource, service);
            if (accessError != null)
            {
                return accessError;
            }

            var snapshotProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var sourceStorageLayerId = ResolveStorageLayerId(snapshot, publication, resource);
            if (sourceStorageLayerId is null)
            {
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Layer '{resource.Metadata.Name ?? layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
            }

            // Validate required parameters (these should already be validated by parameter parsing)
            if (queryParams.ObjectIds.Length == 0)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Invalid query parameters",
                    ["objectIds parameter is required"]);
            }

            var relationship = resource.Relationships.FirstOrDefault(r => ResolveRelationshipId(r) == queryParams.RelationshipId);
            if (relationship is null)
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
            if (!TryResolveRelatedResource(
                    snapshot,
                    service,
                    relationship,
                    out var relatedResource,
                    out var relatedPublication,
                    out var relatedLayerId))
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, relatedLayerId);
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Related layer {relatedLayerId} not found in service '{serviceId}'");
            }

            var resolvedRelatedResource = relatedResource!;
            var resolvedRelatedPublication = relatedPublication!;

            accessError = AccessPolicyHelpers.RequireResourceAccess(httpContext, resolvedRelatedResource, service);
            if (accessError != null)
            {
                return accessError;
            }

            var relatedStorageLayerId = ResolveStorageLayerId(snapshot, resolvedRelatedPublication, resolvedRelatedResource);
            if (relatedStorageLayerId is null)
            {
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Related layer {relatedLayerId} is not bound to a storage layer.");
            }

            var objectIds = queryParams.ObjectIds;
            var outputSrid = await _queryServices.ResolveSridAsync(validatedParams.OutSr, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.OutSr) && !outputSrid.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext,
                    "Invalid output spatial reference",
                    [$"Unsupported outSR value: {validatedParams.OutSr}"]);
            }

            outputSrid ??= resolvedRelatedResource.ReadSrid();

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
                    var translationResult = _filterExpressionService.Translate(parseResult.Expression, resolvedRelatedResource);
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
                relatedStorageLayerId.Value,
                sqlFilter);

            // Execute related query
            QueryResult<Feature> result = await _relatedRecordsService.ExecuteRelatedQueryAsync(sourceStorageLayerId.Value, relatedQuery, cancellationToken);

            // Group results by origin object ID
            var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resolvedRelatedResource);
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
            return StandardErrorHelpers.CreateFromException(_httpContextAccessor.HttpContext!, ex);
        }
        catch (Honua.Core.Exceptions.ValidationException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            return StandardErrorHelpers.CreateFromException(_httpContextAccessor.HttpContext!, ex);
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            var httpContext = _httpContextAccessor.HttpContext!;
            if (IsClientSafeInvalidOperation(ex))
            {
                return StandardErrorHelpers.CreateBadRequest(httpContext, "Invalid query parameters");
            }

            return StandardErrorHelpers.CreateInternalServerError(httpContext, "Related records query execution failed");
        }
        catch (Exception ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            var httpContext = _httpContextAccessor.HttpContext!;
            return StandardErrorHelpers.CreateInternalServerError(httpContext, "Related records query execution failed");
        }
    }

    private static bool IsClientSafeInvalidOperation(InvalidOperationException exception)
    {
        if (string.IsNullOrWhiteSpace(exception.Message))
        {
            return false;
        }

        return exception.Message.StartsWith("Invalid related query", StringComparison.OrdinalIgnoreCase);
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

    private static int ResolveRelationshipId(MetadataV2Relationship relationship)
        => relationship.EsriRelationshipId ?? FeatureServerEndpoints.StableStringHash(relationship.Id);

    private static bool TryResolveRelatedResource(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        MetadataV2Relationship relationship,
        out MetadataV2Resource? resource,
        out MetadataV2Publication? publication,
        out int layerId)
    {
        resource = null;
        publication = null;
        layerId = -1;

        if (!snapshot.Index.ResourcesById.TryGetValue(relationship.RelatedResourceId, out resource))
        {
            return false;
        }

        var relatedResourceId = resource.Metadata.Id;
        publication = snapshot.PublicationsForService(service.Metadata.Id)
            .FirstOrDefault(pub => string.Equals(pub.ResourceId, relatedResourceId, StringComparison.OrdinalIgnoreCase));
        if (publication is null)
        {
            return false;
        }

        layerId = publication.LayerIndex
            ?? snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource)
            ?? -1;
        return layerId >= 0;
    }

    private static int? ResolveStorageLayerId(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource resource)
        => snapshot.ResolveStorageLayerId(publication)
           ?? snapshot.ResolveStorageLayerId(resource)
           ?? publication.LayerIndex;
}
