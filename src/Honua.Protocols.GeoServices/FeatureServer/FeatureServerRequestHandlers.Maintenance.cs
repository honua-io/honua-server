// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
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

        // Determine target layer from request or default to layer 0
        var layerIdStr = GetValueString(values, "layerId");
        var layerId = 0;
        if (!string.IsNullOrWhiteSpace(layerIdStr) &&
            int.TryParse(layerIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
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

        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            resource,
            service,
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

        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            resource,
            service,
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

        var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            resource,
            service,
            cancellationToken);
        if (rbacError != null)
        {
            return rbacError;
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

        // Parse calcExpression as JSON array of {field, sqlExpression}
        if (!TryParseCalcExpressionEntries(calcExpression, out var expressions))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid calcExpression parameter",
                ["calcExpression must be a valid JSON array of {field, sqlExpression} objects."]);
        }

        if (expressions.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "calcExpression must contain at least one expression");
        }

        // Validate each expression
        var parsedExpressions = new List<(string Field, object? Value, bool IsFieldRef)>();
        foreach (var expr in expressions)
        {
            if (string.IsNullOrWhiteSpace(expr.Field))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Each calcExpression entry must have a 'field' property");
            }

            if (string.IsNullOrWhiteSpace(expr.SqlExpression))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"calcExpression entry for field '{expr.Field}' must have a 'sqlExpression' property");
            }

            if (!TryParseCalcExpression(expr.SqlExpression, out var value, out var isFieldRef, out var parseError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Unsupported expression for field '{expr.Field}'",
                    [parseError ?? "Expression is not supported."]);
            }

            parsedExpressions.Add((expr.Field, value, isFieldRef));
        }

        // Validate that all target field names exist in the layer schema
        var validFieldNames = new HashSet<string>(
            resource.SchemaFields.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (field, _, _) in parsedExpressions)
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
        query = query with { Limit = limitsOptions.Value.Query.MaxRecordCount };

        // Query features to update
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var queryResult = await featureReader.QueryAsync(storageLayerId.Value, query, cancellationToken);

        if (queryResult.Items.Length == 0)
        {
            var emptyCalcResponse = new CalculateResponse { Success = true, UpdatedFeatureCount = 0 };
            return Results.Json(emptyCalcResponse, FeatureServerJsonContext.Default.CalculateResponse, contentType: "application/json");
        }

        // Apply expressions and build update batch
        var updates = ImmutableArray.CreateBuilder<Feature>(queryResult.Items.Length);
        foreach (var feature in queryResult.Items)
        {
            var newAttrs = feature.Attributes.ToBuilder();
            foreach (var (field, value, isFieldRef) in parsedExpressions)
            {
                if (isFieldRef)
                {
                    var refField = (string)value!;
                    newAttrs[field] = feature.Attributes.TryGetValue(refField, out var refValue) ? refValue : null;
                }
                else
                {
                    newAttrs[field] = value;
                }
            }

            updates.Add(Feature.Create(feature.Id, feature.Geometry, newAttrs.ToImmutable()));
        }

        var editBatch = FeatureEditBatch.Create(updates: updates.ToImmutable());
        var featureWriter = context.RequestServices.GetRequiredService<IFeatureWriter>();
        var editResult = await featureWriter.ApplyEditsAsync(storageLayerId.Value, editBatch, cancellationToken);

        // Invalidate output cache after successful mutations
        if (!editResult.HasErrors && editResult.UpdatedCount > 0)
        {
            var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
            if (cacheInvalidator != null)
            {
                await cacheInvalidator.InvalidateLayerAsync(serviceId, layerId, cancellationToken);
            }
        }

        var calcResponse = new CalculateResponse
        {
            Success = !editResult.HasErrors,
            UpdatedFeatureCount = editResult.UpdatedCount
        };

        return Results.Json(calcResponse, FeatureServerJsonContext.Default.CalculateResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleQueryDomains(
        string serviceId,
        HttpContext context,
        [FromServices] ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryDomains, out var error))
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
            "queryDomains",
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

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
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

        var accessibleLayers = FilterAccessibleLayersV2(context, service, selectedLayers);
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
            .Where(pair => pair.Resource is not null)
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

        var accessibleLayers = FilterAccessibleLayersV2(context, service, allPairs);

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

    /// <summary>
    /// Parses a calc expression value. Supports string literals ('text'), numeric literals,
    /// NULL, and simple field references.
    /// </summary>
    private static bool TryParseCalcExpression(
        string expression,
        out object? value,
        out bool isFieldRef,
        out string? error)
    {
        value = null;
        isFieldRef = false;
        error = null;

        var trimmed = expression.Trim();

        if (string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // String literal: 'text'
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            value = trimmed[1..^1];
            return true;
        }

        // Integer literal
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            value = intValue;
            return true;
        }

        // Decimal literal
        if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            value = doubleValue;
            return true;
        }

        // Field reference: alphanumeric + underscore, starting with letter or underscore
        if (IsValidFieldReference(trimmed))
        {
            value = trimmed;
            isFieldRef = true;
            return true;
        }

        error = $"Expression '{trimmed}' is not supported. Use literals ('text', 42, NULL) or field references.";
        return false;
    }

    private static bool IsValidFieldReference(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
            {
                return false;
            }
        }

        return true;
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

                parsedExpressions.Add(new CalcExpressionEntry
                {
                    Field = field,
                    SqlExpression = sqlExpression
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

        // sqlType is optional (standard|native). Only the value is validated; both map to the
        // ArcGIS SQL filter dialect for parsing/validation purposes.
        var sqlType = GetValueString(values, "sqlType");
        if (!string.IsNullOrWhiteSpace(sqlType) &&
            !string.Equals(sqlType, "standard", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sqlType, "native", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid sqlType parameter",
                ["sqlType must be 'standard' or 'native'."]);
        }

        // Attempt to parse the SQL expression using the filter service
        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
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

        var accessibleLayers = FilterAccessibleLayersV2(context, service, selectedLayers);
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
        // `where` when omitted, matching the most common ArcGIS client usage.
        var sqlType = GetValueString(values, "sqlType");
        if (string.IsNullOrWhiteSpace(sqlType))
        {
            sqlType = "where";
        }

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
