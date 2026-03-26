// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const int DomainSamplingLimit = 256;
    private const int DomainMaxCodedValues = 16;

    private static async Task<IResult> HandleServiceAppend(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.append");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(receivedContentType);
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
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
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

        var (features, parseError) = TryParseEditsArray(values, context);
        if (parseError != null)
        {
            return parseError;
        }

        if (features == null || features.Length == 0)
        {
            var emptyResponse = new AppendResponse { Success = true, NumFeaturesAppended = 0, NumFeaturesFailed = 0 };
            return Results.Json(emptyResponse, FeatureServerJsonContext.Default.AppendResponse, contentType: "application/json");
        }

        return await ExecuteAppendAsync(context, serviceId, layerId, features, cancellationToken);
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
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid append request",
                [readError ?? "Invalid request body."]);
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
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

        var (features, parseError) = TryParseEditsArray(values, context);
        if (parseError != null)
        {
            return parseError;
        }

        if (features == null || features.Length == 0)
        {
            var emptyResponse = new AppendResponse { Success = true, NumFeaturesAppended = 0, NumFeaturesFailed = 0 };
            return Results.Json(emptyResponse, FeatureServerJsonContext.Default.AppendResponse, contentType: "application/json");
        }

        return await ExecuteAppendAsync(context, serviceId, layerId, features, cancellationToken);
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
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
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
        var layer = validationResult.Layer!;
        var accessError = AccessPolicyHelpers.RequireLayerWriteAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var rbacError = await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context,
            service.Name,
            cancellationToken);
        if (rbacError != null)
        {
            return rbacError;
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
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
            layer.Fields.Select(f => f.Name),
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
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
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
                var translationResult = filterService.Translate(parseResult.Expression, layer);
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
        var queryResult = await featureReader.QueryAsync(layer.Id, query, cancellationToken);

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
        var editResult = await featureWriter.ApplyEditsAsync(layer.Id, editBatch, cancellationToken);

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
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryDomains");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
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
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var domains = await BuildDomainsAsync(service, featureReader, cancellationToken);

        var response = new QueryDomainsResponse
        {
            Domains = domains
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryDomainsResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleQueryRelationships(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryRelationships");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
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
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var response = new QueryRelationshipsResponse
        {
            Relationships =
            [
                ..service.Layers.SelectMany(layer =>
                    layer.LayerRelationships.Select(relationship => new ServiceRelationshipInfo
                    {
                        Id = relationship.RelationshipId,
                        Name = relationship.Name,
                        LayerId = layer.Id,
                        RelatedTableId = relationship.RelatedLayerId,
                        Role = relationship.RelationshipType,
                        KeyField = relationship.DestinationForeignKeyField,
                        OriginKeyField = relationship.OriginForeignKeyField,
                        DestinationKeyField = relationship.DestinationForeignKeyField,
                        Description = relationship.Description
                    }))
            ]
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryRelationshipsResponse, contentType: "application/json");
    }

    private static async Task<DomainInfo[]> BuildDomainsAsync(
        ServiceDefinition service,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        var domains = new List<DomainInfo>();

        foreach (var layer in service.Layers)
        {
            foreach (var field in layer.Fields.Where(static candidate => !candidate.IsGeometry))
            {
                var domain = await TryBuildDomainForFieldAsync(layer, field, featureReader, cancellationToken);
                if (domain != null)
                {
                    domains.Add(domain);
                }
            }
        }

        return domains.ToArray();
    }

    private static async Task<DomainInfo?> TryBuildDomainForFieldAsync(
        LayerDefinition layer,
        FieldDefinition field,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        if (field.Type == FieldType.Boolean)
        {
            return new DomainInfo
            {
                Type = "codedValue",
                Name = $"{layer.Name}_{field.Name}_bool",
                FieldName = field.Name,
                LayerId = layer.Id,
                CodedValues =
                [
                    new DomainCodedValueInfo { Name = "false", Code = "0" },
                    new DomainCodedValueInfo { Name = "true", Code = "1" }
                ]
            };
        }

        if (field.Type != FieldType.String)
        {
            return null;
        }

        var codedValues = await TrySampleCodedValuesAsync(
            featureReader,
            layer.Id,
            field.Name,
            cancellationToken);

        if (codedValues.Length < 2)
        {
            return null;
        }

        return new DomainInfo
        {
            Type = "codedValue",
            Name = $"{layer.Name}_{field.Name}_domain",
            FieldName = field.Name,
            LayerId = layer.Id,
            CodedValues = codedValues
        };
    }

    private static async Task<DomainCodedValueInfo[]> TrySampleCodedValuesAsync(
        IFeatureReader featureReader,
        int layerId,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            OutFields = [fieldName],
            Limit = DomainSamplingLimit
        };

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken);
        if (result.Items.IsDefaultOrEmpty)
        {
            return [];
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in result.Items)
        {
            if (!feature.Attributes.TryGetValue(fieldName, out var raw) || raw == null)
            {
                continue;
            }

            var normalized = NormalizeDomainValue(raw);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            unique.Add(normalized);
            if (unique.Count > DomainMaxCodedValues)
            {
                return [];
            }
        }

        return unique
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Select(static value => new DomainCodedValueInfo
            {
                Name = value,
                Code = value
            })
            .ToArray();
    }

    private static string? NormalizeDomainValue(object raw)
    {
        return raw switch
        {
            null => null,
            bool boolValue => boolValue ? "true" : "false",
            string textValue => string.IsNullOrWhiteSpace(textValue) ? null : textValue.Trim(),
            JsonElement jsonElement => NormalizeJsonElementValue(jsonElement),
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString()
        };
    }

    private static string? NormalizeJsonElementValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToString(),
            _ => value.GetRawText()
        };
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
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
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
        var layer = validationResult.Layer!;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        var whereClause = GetValueString(values, "where");
        if (string.IsNullOrWhiteSpace(whereClause))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "where parameter is required");
        }

        // Attempt to parse the SQL expression using the filter service
        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, whereClause);

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
