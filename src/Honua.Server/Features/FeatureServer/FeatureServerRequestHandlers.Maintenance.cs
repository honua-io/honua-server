// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleServiceAppend(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.append");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                serviceResult.ErrorMessage ?? "Service not found.");
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid append request",
                [readError ?? "Invalid request body."]);
        }

        var edits = GetValueString(values, "edits");
        if (string.IsNullOrWhiteSpace(edits))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "edits parameter is required");
        }

        if (!TryParseJsonPayload(edits, out var jsonError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid edits parameter",
                [jsonError ?? "edits must be valid JSON."]);
        }

        // MVP: validate but return stub response
        var response = new AppendResponse
        {
            Success = true,
            NumFeaturesAppended = 0,
            NumFeaturesFailed = 0
        };

        return Results.Json(response, FeatureServerJsonContext.Default.AppendResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleLayerAppend(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.layerAppend");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid append request",
                [readError ?? "Invalid request body."]);
        }

        var edits = GetValueString(values, "edits");
        if (string.IsNullOrWhiteSpace(edits))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "edits parameter is required");
        }

        if (!TryParseJsonPayload(edits, out var jsonError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid edits parameter",
                [jsonError ?? "edits must be valid JSON."]);
        }

        // MVP: validate but return stub response
        var response = new AppendResponse
        {
            Success = true,
            NumFeaturesAppended = 0,
            NumFeaturesFailed = 0
        };

        return Results.Json(response, FeatureServerJsonContext.Default.AppendResponse, contentType: "application/json");
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
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        var calcExpression = GetValueString(values, "calcExpression");
        if (string.IsNullOrWhiteSpace(calcExpression))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "calcExpression parameter is required");
        }

        if (!TryParseJsonPayload(calcExpression, out var jsonError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid calcExpression parameter",
                [jsonError ?? "calcExpression must be valid JSON."]);
        }

        // MVP: validate the expression and return a stub response
        var response = new CalculateResponse
        {
            Success = true,
            UpdatedFeatureCount = 0
        };

        return Results.Json(response, FeatureServerJsonContext.Default.CalculateResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleQueryDomains(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryDomains");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                serviceResult.ErrorMessage ?? "Service not found.");
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        // MVP: return empty domains list (no coded-value domain metadata yet)
        var response = new QueryDomainsResponse
        {
            Domains = []
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryDomainsResponse, contentType: "application/json");
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
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;
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
}
