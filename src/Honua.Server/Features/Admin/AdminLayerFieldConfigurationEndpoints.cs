// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
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
        if (layerResult.Problem != null || layerResult.Resource == null)
        {
            return layerResult.Problem!;
        }

        var configurations = await fieldConfigurationStore.GetFieldConfigurationsAsync(layerId, cancellationToken)
            .ConfigureAwait(false);
        var response = BuildResponse(layerId, layerResult.Resource, configurations);
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
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (layerResult.Problem != null || layerResult.Resource == null)
        {
            return layerResult.Problem!;
        }

        var preview = AdminLayerConfigurationTargets.Resolve(
            await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false),
            layerId);
        if (preview.IsAmbiguous)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                preview.ConflictDetail ?? "The layer id is published by more than one resource.");
        }

        var validationError = ValidateRequest(layerId, layerResult.Resource, request);
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

        var applied = await ApplyUpdatedFieldConfigurationsAsync(
                graphStore,
                fieldConfigurationStore,
                layerId,
                updates,
                cancellationToken)
            .ConfigureAwait(false);
        if (applied.IsAmbiguous)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                applied.ConflictDetail ?? "The layer id is published by more than one resource.");
        }

        var configurations = applied.Configurations;
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var response = BuildResponse(layerId, layerResult.Resource, configurations);
        return Results.Json(
            ApiResponse<LayerFieldConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFieldConfigurationResponse);
    }

    private static async Task<(MetadataV2Resource? Resource, IResult? Problem)> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerV2Async(layerId, cancellationToken).ConfigureAwait(false);
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
        int layerId,
        MetadataV2Resource resource,
        LayerFieldConfigurationUpdateRequest request)
    {
        if (request.Fields.Count == 0)
        {
            return "At least one field configuration update is required.";
        }

        var knownFields = resource.SchemaFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var primaryIdFieldName = resource.FindPrimaryIdField()?.Name;
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
                return $"Field '{fieldName}' does not exist on layer {layerId}.";
            }

            if (!seen.Add(fieldName))
            {
                return $"Field '{fieldName}' is configured more than once.";
            }

            if (field.Alias?.Trim().Length > MaxFieldAliasLength)
            {
                return $"Alias for field '{fieldName}' must be {MaxFieldAliasLength} characters or fewer.";
            }

            if (field.Hidden == true && IsRequiredProtocolField(layerField, primaryIdFieldName))
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

    /// <summary>
    /// V2 cutover: a field is "required by public protocol contracts" when it is the
    /// primary geometry column, the resource's primary id field (semantic role
    /// <c>id.primary</c>, fall-back name-equality on <c>objectid</c>/<c>id</c>), or the
    /// canonical <c>OBJECTID</c> alias. Mirrors the v1
    /// <c>IsGeometry || layer.ObjectIdFieldName || FieldNames.ObjectId</c> check using
    /// V2 field types and the resolved primary-id field name.
    /// </summary>
    private static bool IsRequiredProtocolField(MetadataV2Field field, string? primaryIdFieldName)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography
           || (primaryIdFieldName is not null
               && field.Name.Equals(primaryIdFieldName, StringComparison.OrdinalIgnoreCase))
           || field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase);

    private static string? ValidateDomain(string fieldName, MetadataV2FieldDomain? domain)
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

        if (domain.CodedValues.Count == 0)
        {
            return $"Domain for field '{fieldName}' must include at least one coded value.";
        }

        if (domain.CodedValues.Count > MaxCodedValues)
        {
            return $"Domain for field '{fieldName}' must include {MaxCodedValues} coded values or fewer.";
        }

        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var codedValue in domain.CodedValues)
        {
            if (codedValue.Code.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return $"Domain code for field '{fieldName}' is required.";
            }

            if (string.IsNullOrWhiteSpace(codedValue.Name) || codedValue.Name.Length > MaxDomainLabelLength)
            {
                return $"Domain label for field '{fieldName}' must be 1 to {MaxDomainLabelLength} characters.";
            }

            if (!seenCodes.Add(codedValue.Code.GetRawText()))
            {
                return $"Domain code '{codedValue.Code.GetRawText()}' for field '{fieldName}' is duplicated.";
            }
        }

        return null;
    }

    private static LayerFieldConfigurationResponse BuildResponse(
        int layerId,
        MetadataV2Resource resource,
        IReadOnlyList<LayerFieldConfiguration> configurations)
    {
        var configurationMap = configurations.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        // V2 cutover: MetadataV2Field has no Domain/IsHidden — those are now operator-only
        // overrides held in the configuration store. When no override row exists the
        // response falls back to Description for alias, null Domain, and Hidden=false
        // (the V2 baseline; v1 used to read these off the FieldDefinition itself).
        var fields = resource.SchemaFields
            .Select(field =>
            {
                configurationMap.TryGetValue(field.Name, out var configuration);
                return new LayerFieldConfigurationItem
                {
                    Name = field.Name,
                    Type = field.Type.ToString(),
                    Alias = configuration is null ? field.Description : configuration.Alias,
                    Domain = configuration?.Domain,
                    Hidden = configuration?.Hidden ?? false
                };
            })
            .ToArray();

        return new LayerFieldConfigurationResponse
        {
            LayerId = layerId,
            Fields = fields
        };
    }

    /// <summary>
    /// Persists field configuration for the single resource that <paramref name="layerId"/> addresses.
    /// An ambiguous id is refused before either store is written.
    /// </summary>
    internal static async Task<AdminLayerFieldApplyResult> ApplyUpdatedFieldConfigurationsAsync(
        IMetadataV2GraphStore graphStore,
        ILayerFieldConfigurationStore fieldConfigurationStore,
        int layerId,
        IReadOnlyList<LayerFieldConfigurationUpdate> updates,
        CancellationToken cancellationToken)
    {
        var preview = AdminLayerConfigurationTargets.Resolve(
            await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false),
            layerId);
        if (preview.IsAmbiguous)
        {
            return AdminLayerFieldApplyResult.Ambiguous(preview.ConflictDetail!);
        }

        var configurations = await fieldConfigurationStore.UpdateFieldConfigurationsAsync(
                layerId,
                updates,
                cancellationToken)
            .ConfigureAwait(false);

        var written = await AdminLayerConfigurationTargets.WriteAsync(
            graphStore,
            layerId,
            resource => ApplyFieldConfigurations(resource, configurations),
            cancellationToken).ConfigureAwait(false);
        if (written.IsAmbiguous)
        {
            return AdminLayerFieldApplyResult.Ambiguous(written.ConflictDetail!);
        }

        return new AdminLayerFieldApplyResult(false, null, configurations);
    }

    private static MetadataV2Resource ApplyFieldConfigurations(
        MetadataV2Resource resource,
        IReadOnlyList<LayerFieldConfiguration> configurations)
    {
        var configurationMap = configurations.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var fields = resource.SchemaFields
            .Select(field => configurationMap.TryGetValue(field.Name, out var configuration)
                ? field with
                {
                    Alias = configuration.Alias,
                    Domain = configuration.Domain,
                    Hidden = configuration.Hidden
                }
                : field)
            .ToArray();
        return resource with { SchemaFields = fields };
    }

    private static string? NormalizeAlias(string? alias)
    {
        var trimmed = alias?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Result of applying a layer field-configuration update.</summary>
/// <param name="IsAmbiguous">True when the layer id addresses more than one resource and nothing was written.</param>
/// <param name="ConflictDetail">Conflict explanation when <paramref name="IsAmbiguous"/> is true.</param>
/// <param name="Configurations">Field configurations written when the layer id was unique.</param>
internal readonly record struct AdminLayerFieldApplyResult(
    bool IsAmbiguous,
    string? ConflictDetail,
    IReadOnlyList<LayerFieldConfiguration> Configurations)
{
    /// <summary>Creates a conflict result that did not write.</summary>
    public static AdminLayerFieldApplyResult Ambiguous(string detail)
        => new(true, detail, Array.Empty<LayerFieldConfiguration>());
}
