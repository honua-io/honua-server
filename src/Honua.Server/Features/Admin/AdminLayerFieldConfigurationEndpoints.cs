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
                field.Hidden,
                field.DefaultValue))
            .ToArray();

        // The field-config store persists alias, hidden, and the full domain JSONB — so the new
        // range domain + mergePolicy/splitPolicy round-trip through the store as part of the
        // domain document. The per-field defaultValue is authored only onto the canonical
        // resource field below (which round-trips through the graph store); it is NOT written to
        // the layer_fields table.
        // TODO(gapB): if defaultValue must survive a graph rebuild from the field-config store
        // alone, add a layer_fields.default_value JSONB column + plumb it through
        // ILayerFieldConfigurationStore. Today the resource field is the authoritative home.
        var configurations = await fieldConfigurationStore.UpdateFieldConfigurationsAsync(
                layerId,
                updates,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteResourceFieldConfigurationsAsync(
            graphStore,
            layerId,
            configurations,
            cancellationToken).ConfigureAwait(false);
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

        var policyError = ValidateDomainPolicies(fieldName, domain);
        if (policyError != null)
        {
            return policyError;
        }

        if (string.Equals(domain.Type, "range", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateRangeDomain(fieldName, domain);
        }

        if (!string.Equals(domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            return $"Domain for field '{fieldName}' must use type 'codedValue' or 'range'.";
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

    /// <summary>
    /// Validates a <c>range</c> domain: requires a two-element [min, max] numeric range,
    /// ordered min &lt;= max, and forbids coded values on a range domain.
    /// </summary>
    private static string? ValidateRangeDomain(string fieldName, MetadataV2FieldDomain domain)
    {
        if (domain.CodedValues.Count > 0)
        {
            return $"Range domain for field '{fieldName}' must not include coded values.";
        }

        if (domain.Range is not { Count: 2 } range)
        {
            return $"Range domain for field '{fieldName}' must provide a two-element [min, max] range.";
        }

        if (range[0].ValueKind != JsonValueKind.Number || range[1].ValueKind != JsonValueKind.Number)
        {
            return $"Range domain bounds for field '{fieldName}' must be numbers.";
        }

        if (range[0].GetDouble() > range[1].GetDouble())
        {
            return $"Range domain for field '{fieldName}' must have min less than or equal to max.";
        }

        return null;
    }

    /// <summary>
    /// Validates the optional Esri-style <c>mergePolicy</c> / <c>splitPolicy</c> tokens
    /// carried on a domain. Accepts the canonical Esri policy names, case-insensitively.
    /// </summary>
    private static string? ValidateDomainPolicies(string fieldName, MetadataV2FieldDomain domain)
    {
        if (domain.MergePolicy is { Length: > 0 } merge && !IsValidMergePolicy(merge))
        {
            return $"Domain mergePolicy '{merge}' for field '{fieldName}' is invalid. "
                + "Allowed: esriMPTDefaultValue, esriMPTSumValues, esriMPTAreaWeighted.";
        }

        if (domain.SplitPolicy is { Length: > 0 } split && !IsValidSplitPolicy(split))
        {
            return $"Domain splitPolicy '{split}' for field '{fieldName}' is invalid. "
                + "Allowed: esriSPTDefaultValue, esriSPTDuplicate, esriSPTGeometryRatio.";
        }

        return null;
    }

    private static bool IsValidMergePolicy(string value) =>
        value.Equals("esriMPTDefaultValue", StringComparison.OrdinalIgnoreCase)
        || value.Equals("esriMPTSumValues", StringComparison.OrdinalIgnoreCase)
        || value.Equals("esriMPTAreaWeighted", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidSplitPolicy(string value) =>
        value.Equals("esriSPTDefaultValue", StringComparison.OrdinalIgnoreCase)
        || value.Equals("esriSPTDuplicate", StringComparison.OrdinalIgnoreCase)
        || value.Equals("esriSPTGeometryRatio", StringComparison.OrdinalIgnoreCase);

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
                    // DefaultValue is authored onto the canonical resource field (the config store
                    // has no defaultValue column), so it is read back off the resource field.
                    DefaultValue = field.DefaultValue,
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

    private static async Task WriteResourceFieldConfigurationsAsync(
        IMetadataV2GraphStore graphStore,
        int layerId,
        IReadOnlyList<LayerFieldConfiguration> configurations,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var targetResourceIds = snapshot.Graph.Publications
            .Where(publication => publication.Identifier.IsNumeric && publication.LayerIndex == layerId)
            .Select(publication => publication.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (targetResourceIds.Count == 0)
        {
            return;
        }

        var configurationMap = configurations.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!targetResourceIds.Contains(resources[i].Metadata.Id))
            {
                continue;
            }

            var fields = resources[i].SchemaFields
                .Select(field => configurationMap.TryGetValue(field.Name, out var configuration)
                    ? field with
                    {
                        Alias = configuration.Alias,
                        Domain = configuration.Domain,
                        Hidden = configuration.Hidden,
                        // Per-field default value is authored onto the canonical resource field
                        // (which round-trips through the graph store). A null update preserves the
                        // current field default; a JSON null literal clears it.
                        DefaultValue = configuration.DefaultValue is { } incoming
                            ? (incoming.ValueKind == JsonValueKind.Null ? null : incoming)
                            : field.DefaultValue
                    }
                    : field)
                .ToArray();
            resources[i] = resources[i] with { SchemaFields = fields };
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updated = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeAlias(string? alias)
    {
        var trimmed = alias?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
