// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints that author the parts of a layer's canonical metadata that have no other setter:
/// relationships, a GeoServices popupInfo template, and an author-supplied drawingInfo renderer
/// (simple/uniqueValue/classBreaks). All write directly into the Metadata v2 graph resource (relationships
/// onto <see cref="MetadataV2Resource.Relationships"/>; popup/drawing into <c>resource.Extensions</c>), which
/// the FeatureServer/OGC/OData emitters already read back.
/// </summary>
internal static class AdminLayerAuthoringEndpoints
{
    private const string PopupInfoExtensionKey = "geoservices:popupInfo";
    private const string DrawingInfoExtensionKey = "geoservices:drawingInfo";
    private const int MaxRelationships = 64;
    private const int MetadataMutationMaxAttempts = 5;

    public static void MapAdminLayerAuthoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Authoring")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{layerId:int}/popup-info", HandleGetPopupInfo).WithName("GetAdminLayerPopupInfo");
        _ = group.MapPut("/{layerId:int}/popup-info", HandleSetPopupInfo).WithName("SetAdminLayerPopupInfo");
        _ = group.MapGet("/{layerId:int}/drawing-info", HandleGetDrawingInfo).WithName("GetAdminLayerDrawingInfo");
        _ = group.MapPut("/{layerId:int}/drawing-info", HandleSetDrawingInfo).WithName("SetAdminLayerDrawingInfo");
        _ = group.MapGet("/{layerId:int}/relationships", HandleGetRelationships).WithName("GetAdminLayerRelationships");
        _ = group.MapPut("/{layerId:int}/relationships", HandleSetRelationships).WithName("SetAdminLayerRelationships");
    }

    // ---- popupInfo + drawingInfo (resource.Extensions documents) ----------------------------------------

    private static Task<IResult> HandleGetPopupInfo(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        CancellationToken cancellationToken) =>
        GetExtensionAsync(layerId, PopupInfoExtensionKey, context, resourceValidator, graphStore, cancellationToken);

    private static Task<IResult> HandleSetPopupInfo(
        int layerId, JsonElement document, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken) =>
        SetExtensionAsync(layerId, PopupInfoExtensionKey, document, context, resourceValidator, graphStore, cacheInvalidator, cancellationToken);

    private static Task<IResult> HandleGetDrawingInfo(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        CancellationToken cancellationToken) =>
        GetExtensionAsync(layerId, DrawingInfoExtensionKey, context, resourceValidator, graphStore, cancellationToken);

    private static Task<IResult> HandleSetDrawingInfo(
        int layerId, JsonElement document, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken) =>
        SetExtensionAsync(layerId, DrawingInfoExtensionKey, document, context, resourceValidator, graphStore, cacheInvalidator, cancellationToken);

    private static async Task<IResult> GetExtensionAsync(
        int layerId, string key, HttpContext context,
        IResourceValidator resourceValidator, IMetadataV2GraphStore graphStore, CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        JsonElement? document = resource.Extensions is not null
            && resource.Extensions.TryGetValue(key, out var stored)
            && stored.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? stored
                : null;

        return Results.Json(
            ApiResponse<LayerAuthoringDocumentResponse>.CreateSuccess(new LayerAuthoringDocumentResponse
            {
                LayerId = layerId,
                Document = document,
            }),
            LayerAuthoringJsonContext.Default.ApiResponseLayerAuthoringDocumentResponse);
    }

    private static async Task<IResult> SetExtensionAsync(
        int layerId, string key, JsonElement document, HttpContext context,
        IResourceValidator resourceValidator, IMetadataV2GraphStore graphStore,
        OutputCacheInvalidationService cacheInvalidator, CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        // An empty/null body clears the document; otherwise it must be a JSON object.
        var clearing = document.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        if (!clearing && document.ValueKind != JsonValueKind.Object)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest, "The document must be a JSON object (or null to clear).");
        }

        var stored = clearing ? (JsonElement?)null : document.Clone();
        await MutateResourceForLayerAsync(
            graphStore, layerId, res => WithExtension(res, key, stored), cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        return Results.Json(
            ApiResponse<LayerAuthoringDocumentResponse>.CreateSuccess(new LayerAuthoringDocumentResponse
            {
                LayerId = layerId,
                Document = stored,
            }),
            LayerAuthoringJsonContext.Default.ApiResponseLayerAuthoringDocumentResponse);
    }

    private static MetadataV2Resource WithExtension(MetadataV2Resource resource, string key, JsonElement? value)
    {
        var extensions = resource.Extensions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (value is { } element)
        {
            extensions[key] = element;
        }
        else
        {
            extensions.Remove(key);
        }

        return resource with { Extensions = extensions };
    }

    // ---- relationships (resource.Relationships) ---------------------------------------------------------

    private static async Task<IResult> HandleGetRelationships(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerRelationshipResponse>.CreateSuccess(BuildRelationshipResponse(layerId, resource, snapshot)),
            LayerAuthoringJsonContext.Default.ApiResponseLayerRelationshipResponse);
    }

    private static async Task<IResult> HandleSetRelationships(
        int layerId, LayerRelationshipUpdateRequest request, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var (relationships, validationError) = BuildRelationships(layerId, resource, request, snapshot);
        if (validationError != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { Relationships = relationships }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var updated = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var refreshedResource = updated.Graph.Resources.FirstOrDefault(r => r.Metadata.Id == resource.Metadata.Id) ?? resource;
        return Results.Json(
            ApiResponse<LayerRelationshipResponse>.CreateSuccess(BuildRelationshipResponse(layerId, refreshedResource, updated)),
            LayerAuthoringJsonContext.Default.ApiResponseLayerRelationshipResponse);
    }

    private static (MetadataV2Relationship[] Relationships, string? Error) BuildRelationships(
        int layerId, MetadataV2Resource resource, LayerRelationshipUpdateRequest request, MetadataV2GraphSnapshot snapshot)
    {
        if (request.Relationships.Count > MaxRelationships)
        {
            return ([], $"At most {MaxRelationships} relationships are allowed.");
        }

        var originFields = resource.SchemaFields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MetadataV2Relationship>(request.Relationships.Count);

        foreach (var item in request.Relationships)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
            {
                return ([], "Each relationship requires an id and a name.");
            }

            if (!seenIds.Add(item.Id))
            {
                return ([], $"Relationship '{item.Id}' is configured more than once.");
            }

            if (string.IsNullOrWhiteSpace(item.OriginField) || !originFields.Contains(item.OriginField))
            {
                return ([], $"Origin field '{item.OriginField}' does not exist on layer {layerId}.");
            }

            if (!IsValidCardinality(item.Cardinality))
            {
                return ([], $"Relationship '{item.Id}' cardinality must be one-to-one, one-to-many, or many-to-many.");
            }

            var targetPublication = snapshot.Graph.Publications
                .FirstOrDefault(p => p.Identifier.IsNumeric && p.LayerIndex == item.RelatedLayerId);
            if (targetPublication is null)
            {
                return ([], $"Related layer {item.RelatedLayerId} was not found.");
            }

            var targetResource = snapshot.Graph.Resources.FirstOrDefault(r => r.Metadata.Id == targetPublication.ResourceId);
            if (targetResource is null
                || !targetResource.SchemaFields.Any(field => field.Name.Equals(item.DestinationField, StringComparison.OrdinalIgnoreCase)))
            {
                return ([], $"Destination field '{item.DestinationField}' does not exist on related layer {item.RelatedLayerId}.");
            }

            result.Add(new MetadataV2Relationship
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                RelatedResourceId = targetPublication.ResourceId,
                Role = string.IsNullOrWhiteSpace(item.Role) ? "origin" : item.Role,
                Cardinality = item.Cardinality,
                OriginField = item.OriginField,
                DestinationField = item.DestinationField,
                EsriRelationshipId = item.EsriRelationshipId,
            });
        }

        return (result.ToArray(), null);
    }

    private static LayerRelationshipResponse BuildRelationshipResponse(
        int layerId, MetadataV2Resource resource, MetadataV2GraphSnapshot snapshot)
    {
        var items = resource.Relationships.Select(rel => new LayerRelationshipItem
        {
            Id = rel.Id,
            Name = rel.Name,
            Description = rel.Description,
            RelatedLayerId = ResolveLayerIdForResource(rel.RelatedResourceId, snapshot),
            Role = rel.Role,
            Cardinality = rel.Cardinality,
            OriginField = rel.OriginField,
            DestinationField = rel.DestinationField,
            EsriRelationshipId = rel.EsriRelationshipId,
        }).ToArray();

        return new LayerRelationshipResponse { LayerId = layerId, Relationships = items };
    }

    private static int ResolveLayerIdForResource(string resourceId, MetadataV2GraphSnapshot snapshot)
    {
        var publication = snapshot.Graph.Publications
            .FirstOrDefault(p => p.Identifier.IsNumeric && p.ResourceId == resourceId);
        return publication?.LayerIndex ?? -1;
    }

    private static bool IsValidCardinality(string cardinality) =>
        cardinality.Equals("one-to-one", StringComparison.OrdinalIgnoreCase)
        || cardinality.Equals("one-to-many", StringComparison.OrdinalIgnoreCase)
        || cardinality.Equals("many-to-many", StringComparison.OrdinalIgnoreCase);

    // ---- shared helpers ---------------------------------------------------------------------------------

    private static async Task<(MetadataV2Resource? Resource, IResult? Problem)> ValidateLayerAsync(
        int layerId, HttpContext context, IResourceValidator resourceValidator, CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerV2Async(layerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            return (null, ProblemDetailsHelpers.CreateAdminProblem(
                context, statusCode, layerResult.ErrorMessage ?? $"Layer {layerId} not found."));
        }

        return (layerResult.Resource, null);
    }

    /// <summary>
    /// Read-modify-write the resource(s) a layer publishes through, with an optimistic-concurrency retry on
    /// Metadata v2 etag mismatch (background writers can bump the etag between the read and the save).
    /// </summary>
    private static async Task MutateResourceForLayerAsync(
        IMetadataV2GraphStore graphStore, int layerId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var targetResourceIds = snapshot.Graph.Publications
                .Where(p => p.Identifier.IsNumeric && p.LayerIndex == layerId)
                .Select(p => p.ResourceId)
                .ToHashSet(StringComparer.Ordinal);
            if (targetResourceIds.Count == 0)
            {
                return;
            }

            var resources = snapshot.Graph.Resources.ToArray();
            var mutated = false;
            for (var i = 0; i < resources.Length; i++)
            {
                if (targetResourceIds.Contains(resources[i].Metadata.Id))
                {
                    resources[i] = mutate(resources[i]);
                    mutated = true;
                }
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

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply.
            }
        }
    }

    private static bool IsEtagMismatch(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);
}
