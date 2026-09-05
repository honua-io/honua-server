// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleServiceAppend(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.append");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var authorizationError = await RequireServiceWriteAccessBeforeBodyAsync(serviceId, context, cancellationToken);
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid append request",
                [readError ?? "Invalid request body."]);
        }

        // Determine target layer from request or default to layer 0. A malformed
        // layerId must be rejected rather than silently appending to layer 0.
        var layerIdStr = GetValueString(values, "layerId");
        var layerId = 0;
        if (!string.IsNullOrWhiteSpace(layerIdStr))
        {
            if (!int.TryParse(layerIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid layerId parameter",
                    ["layerId must be an integer layer id."]);
            }

            layerId = parsed;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var resource = validationResult.Resource!;
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Insert, service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        // Per-operation authorization (BH3-001/BH3-014): append inserts features.
        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            resource,
            service,
            AuthorizationOperation.Insert,
            cancellationToken);
        if (rbacError != null)
        {
            return rbacError;
        }

        var (features, parseError) = TryParseEditsArray(values, context);
        if (parseError != null)
        {
            return parseError;
        }

        return await ExecuteAppendAsync(context, serviceId, layerId, features ?? [], cancellationToken);
    }

    private static async Task<IResult> HandleLayerAppend(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.layerAppend");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var authorizationError = await RequireLayerWriteAccessBeforeBodyAsync(serviceId, layerId, context, cancellationToken);
        if (authorizationError != null)
        {
            return authorizationError;
        }

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid append request",
                [readError ?? "Invalid request body."]);
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var resource = validationResult.Resource!;
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Insert, service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        // Per-operation authorization (BH3-001/BH3-014): append inserts features.
        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            resource,
            service,
            AuthorizationOperation.Insert,
            cancellationToken);
        if (rbacError != null)
        {
            return rbacError;
        }

        var (features, parseError) = TryParseEditsArray(values, context);
        if (parseError != null)
        {
            return parseError;
        }

        return await ExecuteAppendAsync(context, serviceId, layerId, features ?? [], cancellationToken);
    }

    private static async Task<IResult> ExecuteAppendAsync(
        HttpContext context,
        string serviceId,
        int layerId,
        GeoServicesFeature[] features,
        CancellationToken cancellationToken)
    {
        var editsHandler = context.RequestServices.GetRequiredService<FeatureServerEditsHandler>();
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();

        var request = new ApplyEditsRequest { Adds = features, RollbackOnFailure = false };
        var result = await editsHandler.HandleApplyEditsAsync(
            serviceId, layerId, request, limitsOptions.Value.Edits, cancellationToken);

        // Map ApplyEditsResponse to AppendResponse
        if (result is Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApplyEditsResponse> jsonResult &&
            jsonResult.Value != null)
        {
            var editsResponse = jsonResult.Value;
            var appended = editsResponse.AddResults?.Count(r => r.Success) ?? 0;
            var failed = editsResponse.AddResults?.Count(r => !r.Success) ?? 0;

            var response = new AppendResponse
            {
                Success = editsResponse.Success,
                NumFeaturesAppended = appended,
                NumFeaturesFailed = failed
            };
            return Results.Json(response, FeatureServerJsonContext.Default.AppendResponse, contentType: "application/json");
        }

        // If the edit handler returned a non-JSON error response, pass it through
        return result;
    }

    private static async Task<IResult> HandleCalculate(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.calculate");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var publication = validationResult.Publication!;
        var resource = validationResult.Resource!;
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Update, service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        // Per-operation authorization (BH3-001/BH3-014): calculate is a bulk field update.
        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            resource,
            service,
            AuthorizationOperation.Update,
            cancellationToken);
        if (rbacError != null)
        {
            return rbacError;
        }

        // Calculate is a bulk field update; enforce the same Pro FeatureServer-editing
        // entitlement as every other FeatureServer write entrypoint before doing
        // any read work (the shared edits handler re-checks it per batch below).
        var licenseError = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureServerEditsKey, "FeatureServer editing");
        if (licenseError != null)
        {
            return licenseError;
        }

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = ResolveFeatureServerStorageLayerIdV2(snapshot, publication, resource);
        if (storageLayerId is null)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Layer '{resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
        }

        IReadOnlyDictionary<string, StringValues> values = ToCaseInsensitiveDictionary(context.Request.Query);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var (bodyValues, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
            if (bodyValues == null)
            {
                if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var mergedValues = ToCaseInsensitiveDictionary(context.Request.Query);
            foreach (var pair in bodyValues)
            {
                mergedValues[pair.Key] = pair.Value;
            }

            values = mergedValues;
        }

        var calcExpression = GetValueString(values, "calcExpression");
        if (string.IsNullOrWhiteSpace(calcExpression))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "calcExpression parameter is required");
        }

        // Parse calcExpression as JSON array of {field, sqlExpression} or {field, value}.
        if (!TryParseCalcExpressionEntries(calcExpression, out var expressions))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid calcExpression parameter",
                ["calcExpression must be a valid JSON array of {field, sqlExpression} or {field, value} objects."]);
        }

        if (expressions.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "calcExpression must contain at least one expression");
        }

        // Validate each expression
        var parsedExpressions = new List<(string Field, object? Value, bool HasValue, string? SqlExpression)>();
        foreach (var expr in expressions)
        {
            if (string.IsNullOrWhiteSpace(expr.Field))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Each calcExpression entry must have a 'field' property");
            }

            if (!expr.HasValue && string.IsNullOrWhiteSpace(expr.SqlExpression))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"calcExpression entry for field '{expr.Field}' must have a 'value' or 'sqlExpression' property");
            }

            if (expr.HasValue)
            {
                parsedExpressions.Add((expr.Field, expr.Value, true, null));
            }
            else if (!TryValidateCalcExpression(expr.SqlExpression!, out var parseError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Unsupported expression for field '{expr.Field}'",
                    [parseError ?? "Expression is not supported."]);
            }
            else
            {
                parsedExpressions.Add((expr.Field, null, false, expr.SqlExpression));
            }
        }

        // Validate that all target field names exist in the layer schema
        var validFieldNames = new HashSet<string>(
            resource.SchemaFields.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (field, _, _, _) in parsedExpressions)
        {
            if (!validFieldNames.Contains(field))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Field '{field}' does not exist in layer {layerId}.");
            }
        }

        // Build optional WHERE filter
        var whereClause = GetValueString(values, "where");
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = resource.ReadSrid()
        };

        if (!string.IsNullOrWhiteSpace(whereClause) && whereClause != "1=1")
        {
            var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, whereClause);
            if (!parseResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid where clause",
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
            }

            if (parseResult.Expression != null)
            {
                var translationResult = filterService.Translate(parseResult.Expression, resource);
                if (!translationResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid where clause",
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                query = query with { SqlFilter = translationResult.SqlFilter };
            }
        }

        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var maxRecordCount = limitsOptions.Value.Query.MaxRecordCount;
        // Query one row beyond the transfer limit so a truncated match set is
        // rejected up front instead of silently calculating only the first page.
        query = query with { Limit = maxRecordCount + 1 };

        // Query features to update
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var queryResult = await featureReader.QueryAsync(storageLayerId.Value, query, cancellationToken);

        if (queryResult.Items.Length == 0)
        {
            var emptyCalcResponse = new CalculateResponse { Success = true, UpdatedFeatureCount = 0 };
            return Results.Json(emptyCalcResponse, FeatureServerJsonContext.Default.CalculateResponse, contentType: "application/json");
        }

        if (queryResult.Items.Length > maxRecordCount || queryResult.HasMoreResults)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Too many features match the calculate filter",
                [$"The where clause matches more than {maxRecordCount} features; narrow the filter and retry."]);
        }

        // Apply the expressions per feature and route the writes through the shared
        // FeatureServer edit pipeline (attribute validation, attribute rules, plugin
        // hooks, mutation events, and output-cache invalidation) instead of writing
        // directly through IFeatureWriter.
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var updates = new GeoServicesFeature[queryResult.Items.Length];
        for (var i = 0; i < queryResult.Items.Length; i++)
        {
            var feature = queryResult.Items[i];
            var attributes = new Dictionary<string, object?>(parsedExpressions.Count + 1, StringComparer.OrdinalIgnoreCase)
            {
                [objectIdFieldName] = feature.Attributes.TryGetValue(objectIdFieldName, out var objectIdValue) && objectIdValue is not null
                    ? objectIdValue
                    : feature.Id
            };

            foreach (var (field, value, hasValue, sqlExpression) in parsedExpressions)
            {
                if (hasValue)
                {
                    attributes[field] = value;
                }
                else
                {
                    if (!TryEvaluateCalcExpression(sqlExpression!, feature.Attributes, out var calculatedValue, out var expressionError))
                    {
                        return StandardErrorHelpers.CreateBadRequest(context,
                            $"Unsupported expression for field '{field}'",
                            [expressionError ?? "Expression is not supported."]);
                    }

                    attributes[field] = calculatedValue;
                }
            }

            updates[i] = new GeoServicesFeature { Attributes = attributes };
        }

        var editsHandler = context.RequestServices.GetRequiredService<FeatureServerEditsHandler>();
        var editLimits = limitsOptions.Value.Edits;
        var batchSize = Math.Max(1, Math.Min(editLimits.MaxFeaturesPerEdit, editLimits.MaxEditsPerTransaction));
        var updatedCount = 0;
        var allSucceeded = true;

        for (var offset = 0; offset < updates.Length; offset += batchSize)
        {
            var batch = updates[offset..Math.Min(offset + batchSize, updates.Length)];
            var applyRequest = new ApplyEditsRequest { Updates = batch, RollbackOnFailure = true };
            var batchResult = await editsHandler.HandleApplyEditsAsync(
                serviceId, layerId, applyRequest, editLimits, cancellationToken);

            if (batchResult is not Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApplyEditsResponse> jsonResult ||
                jsonResult.Value is null)
            {
                // A protocol-level error (e.g. license/validation). Surface it directly
                // when nothing has been written yet; otherwise report a partial result.
                if (updatedCount == 0)
                {
                    return batchResult;
                }

                allSucceeded = false;
                break;
            }

            var batchResponse = jsonResult.Value;
            updatedCount += batchResponse.UpdateResults?.Count(static r => r.Success) ?? 0;
            if (!batchResponse.Success)
            {
                allSucceeded = false;
                break;
            }
        }

        var calcResponse = new CalculateResponse
        {
            Success = allSucceeded,
            UpdatedFeatureCount = updatedCount
        };

        return Results.Json(calcResponse, FeatureServerJsonContext.Default.CalculateResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleQueryDomains(
        string serviceId,
        HttpContext context,
        [FromServices] ICommonQueryValidator queryValidator)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);

        // Esri services accept BOTH GET and POST for queryDomains; clients POST large
        // layers arrays that exceed URL limits (honua-server#1825). Merge the form body
        // over the query string so both methods share this read-only handler.
        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var (bodyValues, bodyReadError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
            if (bodyValues == null)
            {
                if (TryGetUnsupportedMediaType(bodyReadError, out var receivedContentType))
                {
                    return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    [bodyReadError ?? "Invalid request body."]);
            }

            foreach (var pair in bodyValues)
            {
                values[pair.Key] = pair.Value;
            }
        }

        if (!TryValidateAllowedParameters(values, queryValidator, AllowedQueryParameters.QueryDomains, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = GetValueString(values, "f");
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "queryDomains",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        if (!TryResolveRequestedServiceLayersV2(service, snapshot, values, out var selectedLayers, out _, out var selectionError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [selectionError ?? "Invalid layer selection."]);
        }

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            selectedLayers.Select(pair => pair.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        var accessibleLayers = FilterAccessibleLayersV2(context, snapshot, service, selectedLayers);
        var domains = accessibleLayers
            .SelectMany(pair => pair.Resource.SchemaFields
                .Where(static field => !field.Hidden && field.Domain is not null)
                .Select(field => MapDomainInfoV2(pair.Publication, pair.Resource, field, snapshot)))
            .ToArray();

        var response = new QueryDomainsResponse
        {
            Domains = domains
        };

        scope.SetSuccess(domains.Length);
        return Results.Json(response, FeatureServerJsonContext.Default.QueryDomainsResponse, contentType: "application/json");
    }

    private static DomainInfo MapDomainInfoV2(
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        MetadataV2Field field,
        MetadataV2GraphSnapshot snapshot)
    {
        var domain = field.Domain!;
        var layerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(resource) ?? -1;

        return new DomainInfo
        {
            Type = domain.Type,
            Name = domain.Name,
            FieldName = field.Name,
            FieldType = MapFieldTypeToGeoServicesV2(field.Type),
            LayerId = layerId,
            CodedValues = domain.CodedValues.Count == 0
                ? null
                : domain.CodedValues
                    .Select(static codedValue => new DomainCodedValueInfo
                    {
                        Name = codedValue.Name,
                        Code = codedValue.Code.Clone()
                    })
                    .ToArray(),
            Range = domain.Range is null
                ? null
                : domain.Range
                    .Select(static value => (object)value.Clone())
                    .ToArray(),
            MergePolicy = domain.MergePolicy,
            SplitPolicy = domain.SplitPolicy
        };
    }

    private static async Task<IResult> HandleQueryRelationships(
        string serviceId,
        HttpContext context,
        [FromServices] ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.Relationships, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "relationships",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var allPairs = snapshot.Index.PublicationsByService[service.Metadata.Id]
            .Select(pub => (Publication: pub, Resource: snapshot.ResolveResource(pub)))
            .Where(pair => snapshot.IsRoutable(pair.Publication))
            .Select(pair => (pair.Publication, Resource: pair.Resource!))
            .ToArray();

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            allPairs.Select(pair => pair.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        var accessibleLayers = FilterAccessibleLayersV2(context, snapshot, service, allPairs);

        // Layer ids of accessible publications. Relationships pointing at non-accessible
        // resources are filtered out (matching the v1 path which gated on layer access).
        var layerIdByResourceId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (publication, resource) in accessibleLayers)
        {
            var id = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(resource) ?? -1;
            if (id < 0) continue;
            // First (primary) publication wins for the resource id mapping.
            layerIdByResourceId.TryAdd(resource.Metadata.Id, id);
        }

        var relationships = new List<ServiceRelationshipInfo>();
        foreach (var (publication, resource) in accessibleLayers)
        {
            if (!layerIdByResourceId.TryGetValue(resource.Metadata.Id, out var originLayerId))
            {
                continue;
            }

            foreach (var relationship in resource.Relationships)
            {
                if (!layerIdByResourceId.TryGetValue(relationship.RelatedResourceId, out var relatedLayerId))
                {
                    continue;
                }

                var relationshipId = relationship.EsriRelationshipId ?? StableStringHash(relationship.Id);
                relationships.Add(new ServiceRelationshipInfo
                {
                    Id = relationshipId,
                    Name = relationship.Name,
                    LayerId = originLayerId,
                    RelatedTableId = relatedLayerId,
                    Role = relationship.Role,
                    KeyField = relationship.DestinationField,
                    OriginKeyField = relationship.OriginField,
                    DestinationKeyField = relationship.DestinationField,
                    Description = relationship.Description
                });
            }
        }

        var response = new QueryRelationshipsResponse
        {
            Relationships = [.. relationships]
        };

        scope.SetSuccess(response.Relationships?.Length ?? 0);
        return Results.Json(response, FeatureServerJsonContext.Default.QueryRelationshipsResponse, contentType: "application/json");
    }

    private static bool TryValidateCalcExpression(string expression, out string? error)
    {
        var parser = new CalcSqlExpressionParser(expression, new Dictionary<string, object?>(), validationOnly: true);
        return parser.TryParse(out _, out error);
    }

    private static bool TryEvaluateCalcExpression(
        string expression,
        IReadOnlyDictionary<string, object?> attributes,
        out object? value,
        out string? error)
    {
        var parser = new CalcSqlExpressionParser(expression, attributes);
        return parser.TryParse(out value, out error);
    }

    private static bool TryParseCalcExpressionEntries(string calcExpression, out CalcExpressionEntry[] expressions)
    {
        expressions = [];

        try
        {
            using var document = JsonDocument.Parse(calcExpression);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsedExpressions = new List<CalcExpressionEntry>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                string? field = null;
                if (element.TryGetProperty("field", out var fieldElement) &&
                    fieldElement.ValueKind != JsonValueKind.Null)
                {
                    if (fieldElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    field = fieldElement.GetString();
                }

                string? sqlExpression = null;
                if (element.TryGetProperty("sqlExpression", out var sqlExpressionElement) &&
                    sqlExpressionElement.ValueKind != JsonValueKind.Null)
                {
                    if (sqlExpressionElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    sqlExpression = sqlExpressionElement.GetString();
                }

                if (sqlExpression is null && element.TryGetProperty("expression", out var expressionElement) &&
                    expressionElement.ValueKind != JsonValueKind.Null)
                {
                    if (expressionElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    sqlExpression = expressionElement.GetString();
                }

                var hasValue = element.TryGetProperty("value", out var valueElement) &&
                               valueElement.ValueKind != JsonValueKind.Undefined;
                object? value = null;
                if (hasValue && !TryReadCalcValue(valueElement, out value))
                {
                    return false;
                }

                parsedExpressions.Add(new CalcExpressionEntry
                {
                    Field = field,
                    SqlExpression = sqlExpression,
                    HasValue = hasValue,
                    Value = value
                });
            }

            expressions = [.. parsedExpressions];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class CalcExpressionEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("field")]
        public string? Field { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("sqlExpression")]
        public string? SqlExpression { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public object? Value { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasValue { get; set; }
    }

    private static bool TryReadCalcValue(JsonElement element, out object? value)
    {
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetBoolean();
                return true;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    value = integer;
                    return true;
                }

                if (element.TryGetDouble(out var number))
                {
                    value = number;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Small, parameter-free SQL scalar evaluator for Esri calculate expressions. It deliberately
    /// supports only value-producing SQL (literals, field references, arithmetic, concatenation,
    /// and common scalar functions), so calculate cannot become a general SQL execution surface.
    /// </summary>
    private sealed class CalcSqlExpressionParser
    {
        private readonly string _expression;
        private readonly IReadOnlyDictionary<string, object?> _attributes;
        private readonly bool _validationOnly;
        private int _position;
        private Token _current;

        public CalcSqlExpressionParser(
            string expression,
            IReadOnlyDictionary<string, object?> attributes,
            bool validationOnly = false)
        {
            _expression = expression;
            _attributes = attributes;
            _validationOnly = validationOnly;
            _current = NextToken();
        }

        public bool TryParse(out object? value, out string? error)
        {
            value = null;
            error = null;
            if (!TryParseConcat(out value, out error) || _current.Kind != TokenKind.End)
            {
                error ??= $"Expression '{_expression.Trim()}' is not a valid SQL scalar expression.";
                return false;
            }

            return true;
        }

        private bool TryParseConcat(out object? value, out string? error)
        {
            if (!TryParseAdditive(out value, out error)) return false;
            while (_current.Kind == TokenKind.Concat)
            {
                Advance();
                if (!TryParseAdditive(out var right, out error)) return false;
                value = $"{value ?? string.Empty}{right ?? string.Empty}";
            }
            return true;
        }

        private bool TryParseAdditive(out object? value, out string? error)
        {
            if (!TryParseMultiplicative(out value, out error)) return false;
            while (_current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var operation = _current.Kind;
                Advance();
                if (!TryParseMultiplicative(out var right, out error) ||
                    !TryApplyNumeric(operation, value, right, out value, out error)) return false;
            }
            return true;
        }

        private bool TryParseMultiplicative(out object? value, out string? error)
        {
            if (!TryParseUnary(out value, out error)) return false;
            while (_current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                var operation = _current.Kind;
                Advance();
                if (!TryParseUnary(out var right, out error) ||
                    !TryApplyNumeric(operation, value, right, out value, out error)) return false;
            }
            return true;
        }

        private bool TryParseUnary(out object? value, out string? error)
        {
            if (_current.Kind == TokenKind.Minus)
            {
                Advance();
                if (!TryParseUnary(out var inner, out error) || !TryGetDecimal(inner, out var number))
                {
                    if (_validationOnly && inner is null)
                    {
                        value = null;
                        error = null;
                        return true;
                    }
                    value = null;
                    error ??= "Unary minus requires a numeric value.";
                    return false;
                }
                value = -number;
                return true;
            }

            return TryParsePrimary(out value, out error);
        }

        private bool TryParsePrimary(out object? value, out string? error)
        {
            value = null;
            error = null;
            if (_current.Kind == TokenKind.Number || _current.Kind == TokenKind.String)
            {
                value = _current.Value;
                Advance();
                return true;
            }

            if (_current.Kind != TokenKind.Identifier)
            {
                if (_current.Kind == TokenKind.LeftParen)
                {
                    Advance();
                    if (!TryParseConcat(out value, out error) || !Expect(TokenKind.RightParen, out error)) return false;
                    return true;
                }

                error = $"Unexpected token in expression '{_expression.Trim()}'.";
                return false;
            }

            var name = _current.Value as string ?? string.Empty;
            Advance();
            if (_current.Kind == TokenKind.LeftParen)
            {
                Advance();
                var arguments = new List<object?>();
                if (_current.Kind != TokenKind.RightParen)
                {
                    do
                    {
                        if (!TryParseConcat(out var argument, out error)) return false;
                        arguments.Add(argument);
                        if (_current.Kind != TokenKind.Comma) break;
                        Advance();
                    } while (true);
                }

                if (!Expect(TokenKind.RightParen, out error)) return false;
                return TryApplyFunction(name, arguments, out value, out error);
            }

            if (name.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                value = null;
                return true;
            }
            if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            if (name.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            // During the validation pass unknown fields are allowed; the layer schema check
            // validates targets, while a missing source field evaluates to SQL NULL.
            value = _attributes.TryGetValue(name, out var attribute) ? attribute : null;
            return true;
        }

        private bool TryApplyFunction(string name, List<object?> arguments, out object? value, out string? error)
        {
            value = null;
            error = null;
            var isTextFunction = name.Equals("UPPER", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("LOWER", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("TRIM", StringComparison.OrdinalIgnoreCase);
            var isLengthFunction = name.Equals("LENGTH", StringComparison.OrdinalIgnoreCase);
            var isCoalesceFunction = name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase) ||
                                     name.Equals("NVL", StringComparison.OrdinalIgnoreCase);
            var isConcatFunction = name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase);

            if (_validationOnly &&
                (isTextFunction || isLengthFunction) &&
                arguments.Count == 1 && arguments[0] is null)
            {
                return true;
            }

            if (isTextFunction)
            {
                var text = arguments.Count == 1
                    ? Convert.ToString(arguments[0], CultureInfo.InvariantCulture)
                    : null;
                if (text is null)
                {
                    error = $"{name} requires one text argument.";
                    return false;
                }
                value = name.Equals("UPPER", StringComparison.OrdinalIgnoreCase) ? text.ToUpperInvariant() :
                    name.Equals("LOWER", StringComparison.OrdinalIgnoreCase) ? text.ToLowerInvariant() : text.Trim();
                return true;
            }

            if (isLengthFunction)
            {
                var text = arguments.Count == 1
                    ? Convert.ToString(arguments[0], CultureInfo.InvariantCulture)
                    : null;
                if (text is null)
                {
                    error = "LENGTH requires one text argument.";
                    return false;
                }
                value = (long)text.Length;
                return true;
            }

            if (isCoalesceFunction)
            {
                if (arguments.Count < 2)
                {
                    error = $"{name} requires at least two arguments.";
                    return false;
                }
                value = arguments.FirstOrDefault(static argument => argument is not null);
                return true;
            }

            if (isConcatFunction)
            {
                value = string.Concat(arguments.Select(static argument => argument?.ToString() ?? string.Empty));
                return true;
            }

            error = $"Function '{name}' is not supported by calculate.";
            return false;
        }

        private bool TryApplyNumeric(TokenKind operation, object? left, object? right, out object? value, out string? error)
        {
            value = null;
            error = null;
            if (_validationOnly)
            {
                value = null;
                return true;
            }

            if (!TryGetDecimal(left, out var leftNumber) || !TryGetDecimal(right, out var rightNumber))
            {
                error = "Arithmetic operators require numeric values.";
                return false;
            }

            if ((operation == TokenKind.Slash || operation == TokenKind.Percent) && rightNumber == 0)
            {
                error = "Division by zero is not allowed.";
                return false;
            }

            value = operation switch
            {
                TokenKind.Plus => leftNumber + rightNumber,
                TokenKind.Minus => leftNumber - rightNumber,
                TokenKind.Star => leftNumber * rightNumber,
                TokenKind.Slash => leftNumber / rightNumber,
                TokenKind.Percent => leftNumber % rightNumber,
                _ => null
            };
            return true;
        }

        private static bool TryGetDecimal(object? value, out decimal number)
        {
            number = 0;
            return value is not null && decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
        }

        private bool Expect(TokenKind kind, out string? error)
        {
            if (_current.Kind == kind)
            {
                Advance();
                error = null;
                return true;
            }
            error = $"Expected {kind} in expression '{_expression.Trim()}'.";
            return false;
        }

        private void Advance() => _current = NextToken();

        private Token NextToken()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position])) _position++;
            if (_position >= _expression.Length) return new(TokenKind.End, null);
            var character = _expression[_position++];
            if (char.IsDigit(character) || character == '.')
            {
                var start = _position - 1;
                while (_position < _expression.Length && (char.IsDigit(_expression[_position]) || _expression[_position] == '.')) _position++;
                var raw = _expression[start.._position];
                return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? new(TokenKind.Number, number)
                    : new(TokenKind.Invalid, raw);
            }
            if (char.IsLetter(character) || character == '_')
            {
                var start = _position - 1;
                while (_position < _expression.Length && (char.IsLetterOrDigit(_expression[_position]) || _expression[_position] == '_')) _position++;
                return new(TokenKind.Identifier, _expression[start.._position]);
            }
            if (character == '\'')
            {
                var builder = new System.Text.StringBuilder();
                while (_position < _expression.Length)
                {
                    var next = _expression[_position++];
                    if (next == '\'' && _position < _expression.Length && _expression[_position] == '\'')
                    {
                        builder.Append('\'');
                        _position++;
                    }
                    else if (next == '\'')
                    {
                        return new(TokenKind.String, builder.ToString());
                    }
                    else builder.Append(next);
                }
                return new(TokenKind.Invalid, builder.ToString());
            }

            return character switch
            {
                '+' => new(TokenKind.Plus, null),
                '-' => new(TokenKind.Minus, null),
                '*' => new(TokenKind.Star, null),
                '/' => new(TokenKind.Slash, null),
                '%' => new(TokenKind.Percent, null),
                '(' => new(TokenKind.LeftParen, null),
                ')' => new(TokenKind.RightParen, null),
                ',' => new(TokenKind.Comma, null),
                '|' when _position < _expression.Length && _expression[_position] == '|' => ConsumeConcat(),
                _ => new(TokenKind.Invalid, character.ToString())
            };
        }

        private Token ConsumeConcat()
        {
            _position++;
            return new(TokenKind.Concat, null);
        }

        private enum TokenKind { End, Invalid, Identifier, Number, String, Plus, Minus, Star, Slash, Percent, Concat, LeftParen, RightParen, Comma }
        private readonly record struct Token(TokenKind Kind, object? Value);
    }

    private static async Task<IResult> HandleValidateSql(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.validateSQL");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var resource = validationResult.Resource!;
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Query, service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        // validateSQL is GET or POST. Merge query and (for POST) form/body values so the
        // Esri parameters can arrive via either transport.
        IReadOnlyDictionary<string, StringValues> values = ToCaseInsensitiveDictionary(context.Request.Query);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var (bodyValues, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
            if (bodyValues == null)
            {
                if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var mergedValues = ToCaseInsensitiveDictionary(context.Request.Query);
            foreach (var pair in bodyValues)
            {
                mergedValues[pair.Key] = pair.Value;
            }

            values = mergedValues;
        }

        // Esri's validateSQL uses the `sql` parameter. We tolerate the legacy `where` alias
        // for backward compatibility.
        var sqlExpression = GetValueString(values, "sql");
        if (string.IsNullOrWhiteSpace(sqlExpression))
        {
            sqlExpression = GetValueString(values, "where");
        }

        if (string.IsNullOrWhiteSpace(sqlExpression))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "sql parameter is required");
        }

        // sqlType is optional and overloaded across two distinct Esri concepts (#1901):
        //   * a SQL-dialect selector (standard|native): standardized SQL vs native DBMS SQL;
        //   * the canonical validateSQL clause-type enum the real ArcGIS clients send
        //     (esriSQLTypeWhere/OrderBy/Expression) plus its short forms (where/orderBy/
        //     expression) and the ArcGIS API for Python's `statement` alias.
        // Accept both vocabularies uniformly with the service-level endpoint. Dialect values
        // (and an omitted sqlType) validate as a where-style ArcGIS SQL expression; clause-type
        // values route through the shared service validator so orderBy is validated correctly.
        var sqlType = GetValueString(values, "sqlType");
        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();

        if (!string.IsNullOrWhiteSpace(sqlType) && !IsSqlDialectType(sqlType))
        {
            var clauseType = NormalizeServiceSqlType(sqlType);
            if (!IsSupportedServiceSqlType(clauseType))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid sqlType parameter",
                    ["sqlType must be a SQL dialect ('standard'/'native') or a clause type ('where'/'orderBy'/'expression', or the canonical 'esriSQLType*' enum)."]);
            }

            var (isValid, validationError) = ValidateServiceSql(filterService, sqlExpression, clauseType, resource);
            var clauseResponse = new ValidateSqlResponse
            {
                IsValidSql = isValid,
                ValidationError = isValid ? null : validationError
            };
            return Results.Json(clauseResponse, FeatureServerJsonContext.Default.ValidateSqlResponse, contentType: "application/json");
        }

        // Dialect (standard|native) or omitted: parse the SQL expression using the filter service.
        var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, sqlExpression);

        if (!parseResult.IsSuccess)
        {
            var response = new ValidateSqlResponse
            {
                IsValidSql = false,
                ValidationError = parseResult.ErrorMessage ?? "Invalid SQL syntax."
            };
            return Results.Json(response, FeatureServerJsonContext.Default.ValidateSqlResponse, contentType: "application/json");
        }

        var validResponse = new ValidateSqlResponse
        {
            IsValidSql = true,
            ValidationError = null
        };
        return Results.Json(validResponse, FeatureServerJsonContext.Default.ValidateSqlResponse, contentType: "application/json");
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="sqlType"/> names a SQL dialect
    /// (<c>standard</c> = standardized SQL, <c>native</c> = native DBMS SQL) rather than a
    /// validateSQL clause type. Both dialects map to the ArcGIS SQL filter dialect for
    /// parsing/validation purposes.
    /// </summary>
    private static bool IsSqlDialectType(string sqlType)
        => string.Equals(sqlType, "standard", StringComparison.OrdinalIgnoreCase)
           || string.Equals(sqlType, "native", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Service-level <c>validateSQL</c> (#1446). Esri's canonical validateSQL lives at the
    /// FeatureServer service root and takes <c>sql</c> plus <c>sqlType</c> (one of
    /// <c>where</c>, <c>orderBy</c>, or <c>expression</c>). It reuses the same SQL parsing /
    /// validation the layer-level route uses, validating against a representative accessible
    /// layer of the service.
    /// </summary>
    private static async Task<IResult> HandleServiceValidateSql(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.validateSQL.service");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var queryValues = ToCaseInsensitiveDictionary(context.Request.Query);
        if (!TryResolveRequestedServiceLayersV2(service, snapshot, queryValues, out var selectedLayers, out _, out var selectionError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [selectionError ?? "Invalid layer selection."]);
        }

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            selectedLayers.Select(pair => pair.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        var accessibleLayers = FilterAccessibleLayersV2(context, snapshot, service, selectedLayers);
        if (accessibleLayers.Length == 0)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Service '{serviceId}' has no accessible layers to validate SQL against.");
        }

        // validateSQL is GET or POST. Merge query and (for POST) form/body values so the
        // Esri parameters can arrive via either transport.
        IReadOnlyDictionary<string, StringValues> values = queryValues;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var (bodyValues, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
            if (bodyValues == null)
            {
                if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var mergedValues = ToCaseInsensitiveDictionary(context.Request.Query);
            foreach (var pair in bodyValues)
            {
                mergedValues[pair.Key] = pair.Value;
            }

            values = mergedValues;
        }

        var sqlExpression = GetValueString(values, "sql");
        if (string.IsNullOrWhiteSpace(sqlExpression))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "sql parameter is required");
        }

        // Esri's service-level sqlType is one of where|orderBy|expression. It defaults to
        // `where` when omitted, matching the most common ArcGIS client usage. Real ArcGIS
        // clients send the canonical enum (esriSQLTypeWhere/OrderBy/Expression), so map those
        // to the short forms while still accepting the short forms directly (#1858).
        var sqlType = GetValueString(values, "sqlType");
        sqlType = string.IsNullOrWhiteSpace(sqlType) ? "where" : NormalizeServiceSqlType(sqlType);

        if (!IsSupportedServiceSqlType(sqlType))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid sqlType parameter",
                ["sqlType must be 'where', 'orderBy', or 'expression'."]);
        }

        // Validate against a representative accessible layer of the service.
        var representativeResource = accessibleLayers[0].Resource;
        var (isValid, validationError) = ValidateServiceSql(
            context.RequestServices.GetRequiredService<IFilterExpressionService>(),
            sqlExpression,
            sqlType,
            representativeResource);

        var response = new ValidateSqlResponse
        {
            IsValidSql = isValid,
            ValidationError = isValid ? null : validationError
        };
        return Results.Json(response, FeatureServerJsonContext.Default.ValidateSqlResponse, contentType: "application/json");
    }

    private static bool IsSupportedServiceSqlType(string sqlType)
        => string.Equals(sqlType, "where", StringComparison.OrdinalIgnoreCase)
           || string.Equals(sqlType, "orderBy", StringComparison.OrdinalIgnoreCase)
           || string.Equals(sqlType, "expression", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps the canonical Esri <c>sqlType</c> enum values
    /// (<c>esriSQLTypeWhere</c>/<c>esriSQLTypeOrderBy</c>/<c>esriSQLTypeExpression</c>) that real
    /// ArcGIS clients send to the short forms (<c>where</c>/<c>orderBy</c>/<c>expression</c>) the
    /// rest of the pipeline uses. The ArcGIS API for Python's <c>statement</c> alias maps to
    /// <c>expression</c> (full-statement validation). Short forms and any unrecognized value pass
    /// through unchanged so the caller still validates them (#1858, #1901).
    /// </summary>
    private static string NormalizeServiceSqlType(string sqlType)
    {
        if (string.Equals(sqlType, "esriSQLTypeWhere", StringComparison.OrdinalIgnoreCase))
        {
            return "where";
        }

        if (string.Equals(sqlType, "esriSQLTypeOrderBy", StringComparison.OrdinalIgnoreCase))
        {
            return "orderBy";
        }

        if (string.Equals(sqlType, "esriSQLTypeExpression", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sqlType, "statement", StringComparison.OrdinalIgnoreCase))
        {
            return "expression";
        }

        return sqlType;
    }

    /// <summary>
    /// Validates a SQL string for the given Esri <c>sqlType</c> against a layer schema.
    /// <c>where</c>/<c>expression</c> are parsed and translated through the shared filter
    /// service; <c>orderBy</c> is validated through the shared orderBy parser.
    /// </summary>
    private static (bool IsValid, string? Error) ValidateServiceSql(
        IFilterExpressionService filterService,
        string sqlExpression,
        string sqlType,
        MetadataV2Resource resource)
    {
        if (string.Equals(sqlType, "orderBy", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                OrderByParsing.ParseFeatureServerOrderBy(
                    sqlExpression,
                    resource,
                    FeatureServerOrderByFields.AllowedCoreOrderByFields);
                return (true, null);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message);
            }
        }

        // where + expression both validate as ArcGIS SQL filter expressions. Parse for
        // syntax, then translate against the layer schema so unknown fields / type
        // mismatches are reported as invalid rather than passing a syntactic-only check.
        var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, sqlExpression);
        if (!parseResult.IsSuccess)
        {
            return (false, parseResult.ErrorMessage ?? "Invalid SQL syntax.");
        }

        if (parseResult.Expression != null)
        {
            var translationResult = filterService.Translate(parseResult.Expression, resource);
            if (!translationResult.IsSuccess)
            {
                return (false, translationResult.ErrorMessage ?? "Invalid SQL expression for the service schema.");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Parses the "edits" parameter from request values into a feature array.
    /// Returns a tuple of (features, error). On success, error is null.
    /// </summary>
    private static (GeoServicesFeature[]? Features, IResult? Error) TryParseEditsArray(
        IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        HttpContext context)
    {
        var edits = GetValueString(values, "edits");
        if (string.IsNullOrWhiteSpace(edits))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "edits parameter is required"));
        }

        GeoServicesFeature[]? features;
        try
        {
            features = JsonSerializer.Deserialize(edits, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
        }
        catch (JsonException)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid edits parameter",
                ["edits must be a valid JSON array of features."]));
        }

        return (features, null);
    }
}
