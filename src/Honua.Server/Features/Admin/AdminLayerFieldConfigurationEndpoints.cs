// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for persisted layer field configuration.
/// </summary>
internal static class AdminLayerFieldConfigurationEndpoints
{
    private const int MaxFieldAliasLength = 256;
    private const int MaxDomainNameLength = 128;
    private const int MaxDomainLabelLength = 256;
    private const int MaxCodedValues = 512;

    /// <summary>
    /// Maps layer field configuration endpoints to the admin metadata API group.
    /// </summary>
    public static void MapAdminLayerFieldConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Fields")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{layerId:int}/fields", HandleGetLayerFields)
            .WithName("GetAdminLayerFields")
            .WithSummary("Get persisted layer field configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPut("/{layerId:int}/fields", HandleUpdateLayerFields)
            .WithName("UpdateAdminLayerFields")
            .WithSummary("Update persisted layer field aliases, coded-value domains, and visibility")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<IResult> HandleGetLayerFields(
        int layerId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] ILayerFieldConfigurationStore fieldConfigurationStore,
        CancellationToken cancellationToken)
    {
        var layerResult = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (layerResult.Problem != null || layerResult.Layer == null)
        {
            return layerResult.Problem!;
        }

        var configurations = await fieldConfigurationStore.GetFieldConfigurationsAsync(layerId, cancellationToken)
            .ConfigureAwait(false);
        var response = BuildResponse(layerResult.Layer, configurations);
        return Results.Json(
            ApiResponse<LayerFieldConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFieldConfigurationResponse);
    }

    private static async Task<IResult> HandleUpdateLayerFields(
        int layerId,
        LayerFieldConfigurationUpdateRequest request,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] ILayerFieldConfigurationStore fieldConfigurationStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (layerResult.Problem != null || layerResult.Layer == null)
        {
            return layerResult.Problem!;
        }

        var validationError = ValidateRequest(layerResult.Layer, request);
        if (validationError != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        var updates = request.Fields
            .Select(field => new LayerFieldConfigurationUpdate(
                field.Name.Trim(),
                NormalizeAlias(field.Alias),
                field.Domain,
                field.Hidden))
            .ToArray();

        var configurations = await fieldConfigurationStore.UpdateFieldConfigurationsAsync(
                layerId,
                updates,
                cancellationToken)
            .ConfigureAwait(false);

        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var response = BuildResponse(layerResult.Layer, configurations);
        return Results.Json(
            ApiResponse<LayerFieldConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFieldConfigurationResponse);
    }

    private static async Task<(LayerDefinition? Layer, IResult? Problem)> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found.";
            return (null, ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message));
        }

        return (layerResult.Resource, null);
    }

    private static string? ValidateRequest(
        LayerDefinition layer,
        LayerFieldConfigurationUpdateRequest request)
    {
        if (request.Fields.Count == 0)
        {
            return "At least one field configuration update is required.";
        }

        var knownFields = layer.Fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in request.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                return "Field name is required.";
            }

            var fieldName = field.Name.Trim();
            if (!knownFields.TryGetValue(fieldName, out var layerField))
            {
                return $"Field '{fieldName}' does not exist on layer {layer.Id}.";
            }

            if (!seen.Add(fieldName))
            {
                return $"Field '{fieldName}' is configured more than once.";
            }

            if (field.Alias?.Trim().Length > MaxFieldAliasLength)
            {
                return $"Alias for field '{fieldName}' must be {MaxFieldAliasLength} characters or fewer.";
            }

            if (field.Hidden == true && IsRequiredProtocolField(layer, layerField))
            {
                return $"Field '{fieldName}' cannot be hidden because it is required by public protocol contracts.";
            }

            var domainError = ValidateDomain(fieldName, field.Domain);
            if (domainError != null)
            {
                return domainError;
            }
        }

        return null;
    }

    private static bool IsRequiredProtocolField(LayerDefinition layer, FieldDefinition field)
        => field.IsGeometry
           || field.Name.Equals(layer.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase)
           || field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase);

    private static string? ValidateDomain(string fieldName, FieldDomainDefinition? domain)
    {
        if (domain == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(domain.Name) || domain.Name.Length > MaxDomainNameLength)
        {
            return $"Domain name for field '{fieldName}' must be 1 to {MaxDomainNameLength} characters.";
        }

        if (!string.Equals(domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            return $"Domain for field '{fieldName}' must use type 'codedValue'.";
        }

        if (domain.CodedValues is not { Length: > 0 } codedValues)
        {
            return $"Domain for field '{fieldName}' must include at least one coded value.";
        }

        if (codedValues.Length > MaxCodedValues)
        {
            return $"Domain for field '{fieldName}' must include {MaxCodedValues} coded values or fewer.";
        }

        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var codedValue in codedValues)
        {
            if (codedValue.Code == null)
            {
                return $"Domain code for field '{fieldName}' is required.";
            }

            if (string.IsNullOrWhiteSpace(codedValue.Name) || codedValue.Name.Length > MaxDomainLabelLength)
            {
                return $"Domain label for field '{fieldName}' must be 1 to {MaxDomainLabelLength} characters.";
            }

            if (!seenCodes.Add(codedValue.Code.ToString() ?? string.Empty))
            {
                return $"Domain code '{codedValue.Code}' for field '{fieldName}' is duplicated.";
            }
        }

        return null;
    }

    private static LayerFieldConfigurationResponse BuildResponse(
        LayerDefinition layer,
        IReadOnlyList<LayerFieldConfiguration> configurations)
    {
        var configurationMap = configurations.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var fields = layer.Fields
            .Select(field =>
            {
                configurationMap.TryGetValue(field.Name, out var configuration);
                return new LayerFieldConfigurationItem
                {
                    Name = field.Name,
                    Type = field.Type.ToString(),
                    Alias = configuration is null ? field.Description : configuration.Alias,
                    Domain = configuration is null ? field.Domain : configuration.Domain,
                    Hidden = configuration is null ? field.IsHidden : configuration.Hidden
                };
            })
            .ToArray();

        return new LayerFieldConfigurationResponse
        {
            LayerId = layer.Id,
            Fields = fields
        };
    }

    private static string? NormalizeAlias(string? alias)
    {
        var trimmed = alias?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
