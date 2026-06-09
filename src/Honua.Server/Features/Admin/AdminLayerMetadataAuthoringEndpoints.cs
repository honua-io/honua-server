// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// Admin endpoints that author the typed sub-records of a layer's canonical metadata that
/// were previously stored &amp; served but had no admin write path ("Bucket B" of the UI gap
/// analysis): display hints (<see cref="MetadataV2ResourceDisplay"/>), editor-tracking /
/// edit-capability (<see cref="MetadataV2ResourceEditing"/>), discovery / catalog metadata
/// (<see cref="MetadataV2ObjectMetadata"/> discovery fields), and CRS / spatial authoring
/// fields (<see cref="MetadataV2ResourceSpatial"/> <c>supportedCrs</c> / <c>storageCrs</c> /
/// <c>storageCrsCoordinateEpoch</c>). All write directly into the Metadata v2 graph resource,
/// mirroring <see cref="AdminLayerAuthoringEndpoints"/>.
/// </summary>
internal static class AdminLayerMetadataAuthoringEndpoints
{
    private const int MetadataMutationMaxAttempts = 5;

    public static void MapAdminLayerMetadataAuthoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Authoring")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{layerId:int}/display", HandleGetDisplay).WithName("GetAdminLayerDisplay");
        _ = group.MapPut("/{layerId:int}/display", HandleSetDisplay).WithName("SetAdminLayerDisplay");

        _ = group.MapGet("/{layerId:int}/editing", HandleGetEditing).WithName("GetAdminLayerEditing");
        _ = group.MapPut("/{layerId:int}/editing", HandleSetEditing).WithName("SetAdminLayerEditing");

        _ = group.MapGet("/{layerId:int}/discovery", HandleGetDiscovery).WithName("GetAdminLayerDiscovery");
        _ = group.MapPut("/{layerId:int}/discovery", HandleSetDiscovery).WithName("SetAdminLayerDiscovery");

        _ = group.MapGet("/{layerId:int}/spatial", HandleGetSpatial).WithName("GetAdminLayerSpatial");
        _ = group.MapPut("/{layerId:int}/spatial", HandleSetSpatial).WithName("SetAdminLayerSpatial");
    }

    // ---- 1. Display hints (MetadataV2ResourceDisplay) ---------------------------------------------------

    private static async Task<IResult> HandleGetDisplay(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerDisplayResponse>.CreateSuccess(BuildDisplayResponse(layerId, resource.Display)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseLayerDisplayResponse);
    }

    private static async Task<IResult> HandleSetDisplay(
        int layerId, LayerDisplayUpdateRequest request, HttpContext context,
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

        // Display field, when supplied non-empty, must reference a declared schema field.
        if (!string.IsNullOrWhiteSpace(request.DisplayField)
            && !resource.SchemaFields.Any(f => f.Name.Equals(request.DisplayField, StringComparison.OrdinalIgnoreCase)))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest,
                $"Display field '{request.DisplayField}' does not exist on layer {layerId}.");
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { Display = ApplyDisplay(res.Display, request) }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerDisplayResponse>.CreateSuccess(BuildDisplayResponse(layerId, refreshed.Display)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseLayerDisplayResponse);
    }

    private static MetadataV2ResourceDisplay ApplyDisplay(MetadataV2ResourceDisplay? existing, LayerDisplayUpdateRequest request)
    {
        var current = existing ?? new MetadataV2ResourceDisplay();
        return current with
        {
            MinScale = request.MinScale ?? current.MinScale,
            MaxScale = request.MaxScale ?? current.MaxScale,
            DefaultVisibility = request.DefaultVisibility ?? current.DefaultVisibility,
            DisplayField = request.DisplayField is null
                ? current.DisplayField
                : (string.IsNullOrWhiteSpace(request.DisplayField) ? null : request.DisplayField),
            Queryable = request.Queryable ?? current.Queryable,
            HasZ = request.HasZ ?? current.HasZ,
            HasM = request.HasM ?? current.HasM,
        };
    }

    private static LayerDisplayResponse BuildDisplayResponse(int layerId, MetadataV2ResourceDisplay? display)
    {
        var d = display ?? new MetadataV2ResourceDisplay();
        return new LayerDisplayResponse
        {
            LayerId = layerId,
            MinScale = d.MinScale,
            MaxScale = d.MaxScale,
            DefaultVisibility = d.DefaultVisibility,
            DisplayField = d.DisplayField,
            Queryable = d.Queryable,
            HasZ = d.HasZ,
            HasM = d.HasM,
        };
    }

    // ---- 2. Editor tracking / edit capability (MetadataV2ResourceEditing) -------------------------------

    private static async Task<IResult> HandleGetEditing(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerEditingResponse>.CreateSuccess(BuildEditingResponse(layerId, resource.Editing)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseLayerEditingResponse);
    }

    private static async Task<IResult> HandleSetEditing(
        int layerId, LayerEditingUpdateRequest request, HttpContext context,
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

        // Editor-tracking field references, when supplied non-empty, must exist on the layer.
        var fieldError = ValidateEditingFields(layerId, resource, request);
        if (fieldError != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, fieldError);
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { Editing = ApplyEditing(res.Editing, request) }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerEditingResponse>.CreateSuccess(BuildEditingResponse(layerId, refreshed.Editing)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseLayerEditingResponse);
    }

    private static string? ValidateEditingFields(int layerId, MetadataV2Resource resource, LayerEditingUpdateRequest request)
    {
        var fields = resource.SchemaFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, value) in new (string, string?)[]
        {
            ("globalIdField", request.GlobalIdField),
            ("creatorField", request.CreatorField),
            ("createdAtField", request.CreatedAtField),
            ("editorField", request.EditorField),
            ("updatedAtField", request.UpdatedAtField),
        })
        {
            if (!string.IsNullOrWhiteSpace(value) && !fields.Contains(value))
            {
                return $"Editor-tracking field '{value}' ({label}) does not exist on layer {layerId}.";
            }
        }

        return null;
    }

    private static MetadataV2ResourceEditing ApplyEditing(MetadataV2ResourceEditing? existing, LayerEditingUpdateRequest request)
    {
        var current = existing ?? new MetadataV2ResourceEditing();
        return current with
        {
            GlobalIdField = ApplyNullableField(current.GlobalIdField, request.GlobalIdField),
            CreatorField = ApplyNullableField(current.CreatorField, request.CreatorField),
            CreatedAtField = ApplyNullableField(current.CreatedAtField, request.CreatedAtField),
            EditorField = ApplyNullableField(current.EditorField, request.EditorField),
            UpdatedAtField = ApplyNullableField(current.UpdatedAtField, request.UpdatedAtField),
            CanModify = request.CanModify ?? current.CanModify,
            SupportsAttachments = request.SupportsAttachments ?? current.SupportsAttachments,
            SupportsRelatedRecords = request.SupportsRelatedRecords ?? current.SupportsRelatedRecords,
        };
    }

    private static LayerEditingResponse BuildEditingResponse(int layerId, MetadataV2ResourceEditing? editing)
    {
        var e = editing ?? new MetadataV2ResourceEditing();
        return new LayerEditingResponse
        {
            LayerId = layerId,
            GlobalIdField = e.GlobalIdField,
            CreatorField = e.CreatorField,
            CreatedAtField = e.CreatedAtField,
            EditorField = e.EditorField,
            UpdatedAtField = e.UpdatedAtField,
            CanModify = e.CanModify,
            SupportsAttachments = e.SupportsAttachments,
            SupportsRelatedRecords = e.SupportsRelatedRecords,
        };
    }

    // ---- 3. Discovery / catalog metadata (MetadataV2ObjectMetadata discovery fields) --------------------

    private static async Task<IResult> HandleGetDiscovery(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<DiscoveryMetadataResponse>.CreateSuccess(BuildDiscoveryResponse(layerId, null, resource.Metadata)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseDiscoveryMetadataResponse);
    }

    private static async Task<IResult> HandleSetDiscovery(
        int layerId, DiscoveryMetadataUpdateRequest request, HttpContext context,
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

        var validationError = ValidateDiscovery(request);
        if (validationError != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { Metadata = ApplyDiscovery(res.Metadata, request) }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<DiscoveryMetadataResponse>.CreateSuccess(BuildDiscoveryResponse(layerId, null, refreshed.Metadata)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseDiscoveryMetadataResponse);
    }

    private static string? ValidateDiscovery(DiscoveryMetadataUpdateRequest request)
    {
        if (request.ContactPoint?.Email is { Length: > 0 } email && !email.Contains('@', StringComparison.Ordinal))
        {
            return "Contact point email must contain '@'.";
        }

        if (request.Links is { } links)
        {
            foreach (var link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Href) || string.IsNullOrWhiteSpace(link.Rel))
                {
                    return "Each discovery link requires a non-empty href and rel.";
                }
            }
        }

        return null;
    }

    private static MetadataV2ObjectMetadata ApplyDiscovery(MetadataV2ObjectMetadata metadata, DiscoveryMetadataUpdateRequest request)
    {
        return metadata with
        {
            Title = ApplyNullableField(metadata.Title, request.Title),
            Description = ApplyNullableField(metadata.Description, request.Description),
            Keywords = request.Keywords ?? metadata.Keywords,
            Themes = request.Themes ?? metadata.Themes,
            Language = ApplyNullableField(metadata.Language, request.Language),
            License = ApplyNullableField(metadata.License, request.License),
            Attribution = ApplyNullableField(metadata.Attribution, request.Attribution),
            Publisher = ApplyNullableField(metadata.Publisher, request.Publisher),
            ContactPoint = request.ContactPoint is null
                ? metadata.ContactPoint
                : new MetadataV2ContactPoint
                {
                    Name = request.ContactPoint.Name,
                    Email = request.ContactPoint.Email,
                    Url = request.ContactPoint.Url,
                },
            Links = request.Links is null
                ? metadata.Links
                : request.Links.Select(l => new MetadataV2Link
                {
                    Href = l.Href,
                    Rel = l.Rel,
                    Type = l.Type,
                    Title = l.Title,
                    Hreflang = l.Hreflang,
                }).ToArray(),
        };
    }

    private static DiscoveryMetadataResponse BuildDiscoveryResponse(int? layerId, string? serviceName, MetadataV2ObjectMetadata metadata)
    {
        return new DiscoveryMetadataResponse
        {
            LayerId = layerId,
            ServiceName = serviceName,
            Title = metadata.Title,
            Description = metadata.Description,
            Keywords = metadata.Keywords,
            Themes = metadata.Themes,
            Language = metadata.Language,
            License = metadata.License,
            Attribution = metadata.Attribution,
            Publisher = metadata.Publisher,
            ContactPoint = metadata.ContactPoint is null ? null : new DiscoveryContactPoint
            {
                Name = metadata.ContactPoint.Name,
                Email = metadata.ContactPoint.Email,
                Url = metadata.ContactPoint.Url,
            },
            Links = metadata.Links.Select(l => new DiscoveryLink
            {
                Href = l.Href,
                Rel = l.Rel,
                Type = l.Type,
                Title = l.Title,
                Hreflang = l.Hreflang,
            }).ToArray(),
        };
    }

    // ---- 4. CRS / spatial authoring (MetadataV2ResourceSpatial CRS-list / output-CRS fields) ------------

    private static async Task<IResult> HandleGetSpatial(
        int layerId, HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var (resource, problem) = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (problem != null || resource == null)
        {
            return problem!;
        }

        return Results.Json(
            ApiResponse<LayerSpatialResponse>.CreateSuccess(BuildSpatialResponse(layerId, resource.Spatial)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseLayerSpatialResponse);
    }

    private static async Task<IResult> HandleSetSpatial(
        int layerId, LayerSpatialUpdateRequest request, HttpContext context,
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

        var validationError = ValidateSpatial(request);
        if (validationError != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        await MutateResourceForLayerAsync(
            graphStore, layerId, res => res with { Spatial = ApplySpatial(res.Spatial, request) }, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var refreshed = await GetRefreshedResourceAsync(graphStore, resource, cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<LayerSpatialResponse>.CreateSuccess(BuildSpatialResponse(layerId, refreshed.Spatial)),
            LayerMetadataAuthoringJsonContext.Default.ApiResponseLayerSpatialResponse);
    }

    private static string? ValidateSpatial(LayerSpatialUpdateRequest request)
    {
        if (request.SupportedCrs is { } supported)
        {
            foreach (var crs in supported)
            {
                if (crs.Srid is null && string.IsNullOrWhiteSpace(crs.Crs))
                {
                    return "Each supportedCrs entry requires a srid or a crs identifier.";
                }
            }
        }

        if (!request.ClearStorageCrs && request.StorageCrs is { } storage
            && storage.Srid is null && string.IsNullOrWhiteSpace(storage.Crs))
        {
            return "storageCrs requires a srid or a crs identifier.";
        }

        return null;
    }

    private static MetadataV2ResourceSpatial ApplySpatial(MetadataV2ResourceSpatial? existing, LayerSpatialUpdateRequest request)
    {
        // The stored SRID / geometry type / extent (SpatialReference, GeometryType, Bbox,
        // PrimaryGeometryField) are intentionally preserved untouched — only the CRS-list /
        // output-CRS authoring fields are mutated here.
        var current = existing ?? new MetadataV2ResourceSpatial();
        return current with
        {
            SupportedCrs = request.SupportedCrs is null
                ? current.SupportedCrs
                : request.SupportedCrs.Select(ToSpatialReference).ToArray(),
            StorageCrs = request.ClearStorageCrs
                ? null
                : (request.StorageCrs is null ? current.StorageCrs : ToSpatialReference(request.StorageCrs)),
            StorageCrsCoordinateEpoch = request.ClearStorageCrsCoordinateEpoch
                ? null
                : (request.StorageCrsCoordinateEpoch ?? current.StorageCrsCoordinateEpoch),
        };
    }

    private static LayerSpatialResponse BuildSpatialResponse(int layerId, MetadataV2ResourceSpatial? spatial)
    {
        var s = spatial ?? new MetadataV2ResourceSpatial();
        return new LayerSpatialResponse
        {
            LayerId = layerId,
            SpatialReference = ToPayload(s.SpatialReference),
            SupportedCrs = s.SupportedCrs.Select(ToPayload).Where(p => p is not null).Select(p => p!).ToArray(),
            StorageCrs = ToPayload(s.StorageCrs),
            StorageCrsCoordinateEpoch = s.StorageCrsCoordinateEpoch,
        };
    }

    private static MetadataV2SpatialReference ToSpatialReference(SpatialReferencePayload payload) => new()
    {
        Srid = payload.Srid,
        Crs = payload.Crs,
        IsGeographic = payload.IsGeographic,
    };

    private static SpatialReferencePayload? ToPayload(MetadataV2SpatialReference? reference) =>
        reference is null ? null : new SpatialReferencePayload
        {
            Srid = reference.Srid,
            Crs = reference.Crs,
            IsGeographic = reference.IsGeographic,
        };

    // ---- shared helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Apply-with-clear semantics for a single nullable string field: <c>null</c> request value
    /// leaves the stored value unchanged; an empty/whitespace request value clears it; any other
    /// value sets it.
    /// </summary>
    private static string? ApplyNullableField(string? current, string? requested) =>
        requested is null ? current : (string.IsNullOrWhiteSpace(requested) ? null : requested);

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

    private static async Task<MetadataV2Resource> GetRefreshedResourceAsync(
        IMetadataV2GraphStore graphStore, MetadataV2Resource fallback, CancellationToken cancellationToken)
    {
        var updated = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return updated.Graph.Resources.FirstOrDefault(r => r.Metadata.Id == fallback.Metadata.Id) ?? fallback;
    }

    /// <summary>
    /// Read-modify-write the resource(s) a layer publishes through, with an optimistic-concurrency retry
    /// on Metadata v2 etag mismatch. Mirrors <see cref="AdminLayerAuthoringEndpoints"/>.
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
