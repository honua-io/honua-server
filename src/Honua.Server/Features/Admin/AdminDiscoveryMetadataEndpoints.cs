// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static class AdminDiscoveryMetadataEndpoints
{
    private const int MaxTitleLength = 512;
    private const int MaxDescriptionLength = 16384;
    private const int MaxKeywordLength = 256;
    private const int MaxKeywords = 100;
    private const int RecentHistoryLimit = 10;

    public static void MapAdminDiscoveryMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet().HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata").RequireAdminAuthorization();
        group.MapGet("/metadata/layers/{storageLayerId:int}/discovery", GetLayerAsync)
            .WithName("GetAdminLayerDiscovery");
        var layerDiscovery = AdminHttpOperationRouteBinding.ForApi(
            "admin.metadata.layer-discovery.set", string.Empty, "/metadata/layers/{storageLayerId:int}/discovery");
        group.MapMethods(layerDiscovery.RoutePattern, layerDiscovery.Methods, UpdateLayerAsync)
            .WithAdminOperation(layerDiscovery.Definition)
            .AddAgentApprovalFilter(AgentApprovalFilter.MetadataDiscovery);
        group.MapGet("/services/{serviceName}/discovery", GetServiceAsync)
            .WithName("GetAdminServiceDiscovery");
        var serviceDiscovery = AdminHttpOperationRouteBinding.ForApi(
            "admin.metadata.service-discovery.set", string.Empty, "/services/{serviceName}/discovery");
        group.MapMethods(serviceDiscovery.RoutePattern, serviceDiscovery.Methods, UpdateServiceAsync)
            .WithAdminOperation(serviceDiscovery.Definition)
            .AddAgentApprovalFilter(AgentApprovalFilter.MetadataDiscovery);
        group.MapGet("/metadata/layers/{storageLayerId:int}/history", GetLayerHistoryAsync)
            .WithName("GetAdminLayerMetadataHistory");
        group.MapGet("/services/{serviceName}/history", GetServiceHistoryAsync)
            .WithName("GetAdminServiceMetadataHistory");
        group.MapGet("/metadata/history", GetHistoryAsync)
            .WithName("GetAdminMetadataHistory")
            .RequirePlatformAdminAuthority();
        group.MapGet("/metadata/schemas", GetSchemasAsync)
            .WithName("GetAdminMetadataSchemas")
            .RequirePlatformAdminAuthority();
        group.MapPut("/metadata/schemas", UpdateSchemasAsync)
            .WithName("UpdateAdminMetadataSchemas")
            .RequirePlatformAdminAuthority()
            .AddAgentApprovalFilter(AgentApprovalFilter.MetadataDiscovery);
    }

    private static Task<IResult> GetLayerAsync(int storageLayerId, HttpContext context,
        [FromServices] IMetadataV2GraphProvider provider) =>
        ReadAsync(context, provider, snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, storageLayerId));

    private static Task<IResult> GetServiceAsync(string serviceName, HttpContext context,
        [FromServices] IMetadataV2GraphProvider provider) =>
        ReadAsync(context, provider, snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName));

    private static Task<IResult> UpdateLayerAsync(int storageLayerId, DiscoveryMetadataUpdateRequest request, HttpContext context) =>
        WriteAsync(context, request, snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, storageLayerId));

    private static Task<IResult> UpdateServiceAsync(string serviceName, DiscoveryMetadataUpdateRequest request, HttpContext context) =>
        WriteAsync(context, request, snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName));

    private static Task<IResult> GetLayerHistoryAsync(int storageLayerId, [AsParameters] MetadataHistoryPageQuery page,
        HttpContext context) =>
        ReadTargetHistoryAsync(context, snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, storageLayerId), page);

    private static Task<IResult> GetServiceHistoryAsync(string serviceName, [AsParameters] MetadataHistoryPageQuery page,
        HttpContext context) =>
        ReadTargetHistoryAsync(context, snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName), page);

    private static Task<IResult> GetHistoryAsync([AsParameters] MetadataHistoryItemQuery item,
        [AsParameters] MetadataHistoryPageQuery page, HttpContext context) =>
        page.Error is { } error
            ? Task.FromResult(Problem(context, 400, error))
            : ListHistoryAsync(context, page.ToQuery(item.ItemKind, item.ItemId));

    private static async Task<IResult> GetSchemasAsync(HttpContext context, [FromServices] IMetadataV2GraphProvider provider)
    {
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        return RespondSchemas(snapshot);
    }

    private static async Task<IResult> UpdateSchemasAsync(MetadataCustomFieldSchemasUpdateRequest request, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Etag))
            return Problem(context, 400, "An ETag from the schema read is required.");
        if (request.Schemas is null)
            return Problem(context, 400, "Schemas array is required.");

        var schemaErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateSchemas(schemaErrors, request.Schemas);
        if (schemaErrors.Count > 0)
            return Problem(context, 400, string.Join(" ", schemaErrors));

        var store = context.RequestServices.GetRequiredService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (!string.Equals(snapshot.Etag, request.Etag, StringComparison.Ordinal))
            return Problem(context, 409, "Metadata changed. Reload before saving.");

        var updatedGraph = snapshot.Graph with
        {
            CustomFieldSchemas = request.Schemas.Length == 0 ? null : request.Schemas,
            Revision = snapshot.Graph.Revision + 1,
        };
        var graphValidation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!graphValidation.IsValid)
            return Problem(context, 400, string.Join(" ", graphValidation.Errors));

        MetadataV2GraphSnapshot saved;
        try
        {
            saved = await store.SaveAsync(updatedGraph, snapshot.Etag, context.RequestAborted).ConfigureAwait(false);
        }
        catch (MetadataV2GraphConcurrencyException)
        {
            return Problem(context, 409, "Metadata changed. Reload before saving.");
        }

        var cache = context.RequestServices.GetRequiredService<OutputCacheInvalidationService>();
        await cache.InvalidateServiceCatalogAsync(null, null, context.RequestAborted).ConfigureAwait(false);
        return RespondSchemas(saved);
    }

    private static async Task<IResult> ReadTargetHistoryAsync(
        HttpContext context,
        Func<MetadataV2GraphSnapshot, ResourceValidationResult<DiscoveryMetadataTarget>> resolve,
        MetadataHistoryPageQuery page)
    {
        if (page.Error is { } error)
            return Problem(context, 400, error);

        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        if (!target.IsValid)
            return ServiceResourceValidationHelpers.CreateAdminValidationProblem(context, target, "Metadata target not found.");
        if (!IsTargetTenantVisible(context, snapshot, target.Resource!))
            return Problem(context, StatusCodes.Status404NotFound, "Metadata target not found.");

        return await ListHistoryAsync(context, page.ToQuery(target.Resource!.HistoryItemKind, target.Resource!.Metadata.Id))
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ListHistoryAsync(HttpContext context, MetadataFieldHistoryQuery query)
    {
        var historyStore = context.RequestServices.GetService<IMetadataFieldHistoryStore>();
        var page = historyStore is null
            ? new MetadataFieldHistoryPage { Items = [], HasMore = false }
            : await historyStore.ListAsync(query, context.RequestAborted).ConfigureAwait(false);

        return Results.Json(ApiResponse<MetadataFieldHistoryResponse>.CreateSuccess(new()
        {
            Items = page.Items,
            NextBeforeId = page.NextBeforeId,
            HasMore = page.HasMore,
        }), DiscoveryMetadataJsonContext.Default.ApiResponseMetadataFieldHistoryResponse);
    }

    private static async Task<IResult> ReadAsync(HttpContext context, IMetadataV2GraphProvider provider,
        Func<MetadataV2GraphSnapshot, ResourceValidationResult<DiscoveryMetadataTarget>> resolve)
    {
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        if (!target.IsValid)
            return ServiceResourceValidationHelpers.CreateAdminValidationProblem(context, target, "Metadata target not found.");
        if (!IsTargetTenantVisible(context, snapshot, target.Resource!))
            return Problem(context, StatusCodes.Status404NotFound, "Metadata target not found.");

        var history = await ReadRecentHistoryAsync(context, target.Resource!).ConfigureAwait(false);
        return Respond(snapshot, target.Resource!, history);
    }

    private static async Task<IResult> WriteAsync(HttpContext context, DiscoveryMetadataUpdateRequest request,
        Func<MetadataV2GraphSnapshot, ResourceValidationResult<DiscoveryMetadataTarget>> resolve)
    {
        var error = Validate(request);
        if (error is not null)
            return Problem(context, 400, error);
        var store = context.RequestServices.GetRequiredService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        if (!target.IsValid)
            return ServiceResourceValidationHelpers.CreateAdminValidationProblem(context, target, "Metadata target not found.");
        if (!IsTargetTenantVisible(context, snapshot, target.Resource!))
            return Problem(context, StatusCodes.Status404NotFound, "Metadata target not found.");
        if (!string.Equals(snapshot.Etag, request.Etag, StringComparison.Ordinal))
            return Problem(context, 409, "Metadata changed. Reload before saving.");

        if (CustomFieldsError(snapshot, target.Resource!, request) is { } customError)
            return Problem(context, 400, customError);

        MetadataV2GraphSnapshot saved;
        try
        {
            saved = await store.SaveAsync(target.Resource!.Apply(snapshot.Graph, request),
                snapshot.Etag, context.RequestAborted).ConfigureAwait(false);
        }
        catch (MetadataV2GraphConcurrencyException)
        {
            return Problem(context, 409, "Metadata changed. Reload before saving.");
        }
        var cache = context.RequestServices.GetRequiredService<OutputCacheInvalidationService>();
        await cache.InvalidateServiceCatalogAsync(null, null, context.RequestAborted).ConfigureAwait(false);
        var savedTarget = resolve(saved).Resource!;
        var history = await ReadRecentHistoryAsync(context, savedTarget).ConfigureAwait(false);
        return Respond(saved, savedTarget, history);
    }

    // Values the request writes must be declared; values already stored are only checked against their schema.
    private static string? CustomFieldsError(MetadataV2GraphSnapshot snapshot, DiscoveryMetadataTarget target,
        DiscoveryMetadataUpdateRequest request)
    {
        var schemas = snapshot.Graph.CustomFieldSchemas ?? [];
        if (request.CustomFields is null && schemas.Count == 0)
            return null;

        var errors = new List<string>();
        var owner = new MetadataV2CustomFieldOwner(target.Metadata.Id, target.ItemKind,
            request.CustomFields ?? target.Metadata.CustomFields);
        if (request.CustomFields is { Count: > 0 })
            MetadataV2CustomFieldValidation.ValidateDeclaredItemCustomFields(errors, owner, schemas);
        else
            MetadataV2CustomFieldValidation.ValidateRequiredItemCustomFields(errors, owner, schemas);
        return errors.Count > 0 ? string.Join(" ", errors) : null;
    }

    private static async Task<IReadOnlyList<MetadataFieldHistoryEntry>> ReadRecentHistoryAsync(
        HttpContext context,
        DiscoveryMetadataTarget target)
    {
        var historyStore = context.RequestServices.GetService<IMetadataFieldHistoryStore>();
        if (historyStore is null)
            return [];

        var page = await historyStore.ListAsync(new MetadataFieldHistoryQuery
        {
            ItemKind = target.HistoryItemKind,
            ItemId = target.Metadata.Id,
            Limit = RecentHistoryLimit,
        }, context.RequestAborted).ConfigureAwait(false);
        return page.Items;
    }

    private static string? Validate(DiscoveryMetadataUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Etag))
            return "An ETag from the discovery read is required.";
        if (!ValidText(request.Title, MaxTitleLength) || !ValidText(request.Description, MaxDescriptionLength))
            return "Title and description must be within their length limits and contain no invalid control characters.";
        if (request.Keywords is null || request.Keywords.Length > MaxKeywords ||
            request.Keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) || !ValidText(keyword, MaxKeywordLength)))
            return "Supply at most 100 nonempty keywords of at most 256 characters each.";
        return null;
    }

    private static bool ValidText(string? value, int limit) => value is null ||
        (value.Length <= limit && !value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')));

    private static IResult Respond(
        MetadataV2GraphSnapshot snapshot,
        DiscoveryMetadataTarget target,
        IReadOnlyList<MetadataFieldHistoryEntry> recentHistory)
    {
        var applicableSchemas = (snapshot.Graph.CustomFieldSchemas ?? [])
            .Where(schema => schema.AppliesToKind(target.ItemKind))
            .ToArray();
        return Results.Json(ApiResponse<DiscoveryMetadataResponse>.CreateSuccess(new()
        {
            Etag = snapshot.Etag,
            EntityId = target.Metadata.Id,
            Title = target.Metadata.Title,
            Description = target.Metadata.Description,
            Keywords = target.Metadata.Keywords,
            AffectedPublications = target.Publications,
            CustomFields = target.Metadata.CustomFields ?? new Dictionary<string, JsonElement>(),
            CustomFieldSchemas = applicableSchemas,
            RecentHistory = recentHistory,
        }), DiscoveryMetadataJsonContext.Default.ApiResponseDiscoveryMetadataResponse);
    }

    private static IResult RespondSchemas(MetadataV2GraphSnapshot snapshot) =>
        Results.Json(ApiResponse<MetadataCustomFieldSchemasResponse>.CreateSuccess(new()
        {
            Etag = snapshot.Etag,
            Environment = snapshot.Graph.Environment,
            Schemas = snapshot.Graph.CustomFieldSchemas ?? [],
        }), DiscoveryMetadataJsonContext.Default.ApiResponseMetadataCustomFieldSchemasResponse);

    private static bool IsTargetTenantVisible(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        DiscoveryMetadataTarget target)
    {
        if (target.ResourceIds.Count > 0)
        {
            return target.ResourceIds.All(id =>
                snapshot.Index.ResourcesById.TryGetValue(id, out var resource)
                && ServiceResourceValidationHelpers.IsAdminResourceTenantVisible(context, snapshot, resource));
        }

        return target.ServiceIds.Count > 0 && target.ServiceIds.All(id =>
            snapshot.Index.ServicesById.TryGetValue(id, out var service)
            && ServiceResourceValidationHelpers.IsAdminServiceTenantVisible(context, service));
    }

    private static IResult Problem(HttpContext context, int status, string detail) =>
        ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
}
