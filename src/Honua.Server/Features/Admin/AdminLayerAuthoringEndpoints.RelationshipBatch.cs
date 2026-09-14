// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static partial class AdminLayerAuthoringEndpoints
{
    private const int MaxRelationshipBatchLayers = 64;

    private static async Task<IResult> HandleSetRelationshipsBatch(
        LayerRelationshipBatchUpdateRequest request, HttpContext context,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var (saved, problem) = await ApplyRelationshipBatchAsync(
            request, context, graphStore, cacheInvalidator, cancellationToken).ConfigureAwait(false);
        if (problem is not null || saved is null)
        {
            return problem!;
        }

        var response = new LayerRelationshipBatchResponse
        {
            Layers = request.Layers.Select(item => BuildRelationshipResponse(
                item.LayerId, ResolveRelationshipResource(item.LayerId, saved).Resource!, saved)).ToArray(),
        };
        return Results.Json(ApiResponse<LayerRelationshipBatchResponse>.CreateSuccess(response),
            LayerAuthoringJsonContext.Default.ApiResponseLayerRelationshipBatchResponse);
    }

    private static async Task<(MetadataV2GraphSnapshot? Saved, IResult? Problem)> ApplyRelationshipBatchAsync(
        LayerRelationshipBatchUpdateRequest request, HttpContext context,
        IMetadataV2GraphStore graphStore, OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        if (request.Layers is null || request.Layers.Count is < 1 or > MaxRelationshipBatchLayers
            || request.Layers.Any(item => item is null)
            || request.Layers.Select(item => item.LayerId).Distinct().Count() != request.Layers.Count)
        {
            return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                $"Supply between 1 and {MaxRelationshipBatchLayers} distinct layers."));
        }

        for (var attempt = 1; attempt <= MetadataMutationMaxAttempts; attempt++)
        {
            // Resolve every resource and field again after an ETag conflict. Relationships
            // built from an older snapshot must never be applied to a new graph blindly.
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var replacements = new Dictionary<string, MetadataV2Resource>(StringComparer.Ordinal);
            foreach (var item in request.Layers)
            {
                var (resource, resolutionError) = ResolveRelationshipResource(item.LayerId, snapshot);
                if (resource is null || resolutionError is not null)
                {
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                        resolutionError ?? "Layer could not be resolved."));
                }

                var (relationships, error) = BuildRelationships(item.LayerId, resource,
                    new LayerRelationshipUpdateRequest { Relationships = item.Relationships }, snapshot);
                if (error is not null)
                {
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, error));
                }

                if (!replacements.TryAdd(resource.Metadata.Id, resource with { Relationships = relationships }))
                {
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                        "Multiple layer entries resolve to the same resource. Supply each resource once."));
                }
            }

            var candidate = snapshot.Graph with
            {
                Resources = snapshot.Graph.Resources.Select(resource =>
                    replacements.GetValueOrDefault(resource.Metadata.Id, resource)).ToArray(),
                Revision = snapshot.Graph.Revision + 1,
            };
            if (!MetadataV2GraphValidator.Validate(candidate).IsValid)
            {
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                    "The relationship batch violates canonical metadata rules. Composite relationships require reciprocal declarations and read-only resources."));
            }

            MetadataV2GraphSnapshot saved;
            try
            {
                saved = await graphStore.SaveAsync(candidate, snapshot.Etag, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsEtagMismatch(ex))
            {
                continue;
            }

            var affectedLayerIds = saved.Graph.Publications
                .Where(publication => replacements.ContainsKey(publication.ResourceId))
                .Select(publication => saved.ResolveStorageLayerId(publication))
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            await cacheInvalidator.InvalidateServiceCatalogAsync(null, affectedLayerIds, cancellationToken).ConfigureAwait(false);
            return (saved, null);
        }

        return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict,
            "Metadata changed during relationship authoring. Retry against the current catalog."));
    }

    private static (MetadataV2Resource? Resource, string? Error) ResolveRelationshipResource(
        int layerId, MetadataV2GraphSnapshot snapshot)
    {
        // These admin DTOs carry global storage-layer ids, not service-local
        // LayerIndex values. A resource may have several protocol publications.
        var resourceIds = snapshot.PublicationsForStorageLayer(layerId)
            .Where(publication => snapshot.ResolveResource(publication)?.ResourceType
                is MetadataV2ResourceType.FeatureDataset or MetadataV2ResourceType.Table)
            .Select(publication => publication.ResourceId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (resourceIds.Length != 1)
        {
            return (null, $"Layer {layerId} must resolve to exactly one routable storage-backed resource.");
        }

        return snapshot.Index.ResourcesById.TryGetValue(resourceIds[0], out var resource)
            ? (resource, null)
            : (null, $"Layer {layerId} was not found.");
    }
}
