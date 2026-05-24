// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Console;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for Metadata v2 semantic inventory, environment bindings, and release packages.
/// </summary>
internal static class MetadataReleaseEndpoints
{
    /// <summary>
    /// Maps Metadata v2 release package endpoints.
    /// </summary>
    public static void MapMetadataReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Releases")
            .RequireAdminAuthorization();

        group.MapGet("/environments/{environment}/inventory", HandleGetInventory)
            .WithDisplayName("Get Metadata Semantic Inventory")
            .WithSummary("Returns the active Metadata v2 revision-stamped semantic inventory for an environment.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<MetadataSemanticInventoryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/environment-bindings/query", HandleQueryEnvironmentBindings)
            .WithDisplayName("Query Metadata Environment Bindings")
            .WithSummary("Returns secret-safe binding summaries for semantic ids across environments.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<MetadataEnvironmentBindingsResponse>();

        group.MapPost("/release-packages", HandleCreateReleasePackage)
            .WithDisplayName("Create Metadata Release Package")
            .WithSummary("Persists a Metadata v2 release package for semantic cross-environment promotion.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<MetadataReleasePackage>(StatusCodes.Status201Created);

        group.MapGet("/release-packages/{packageId:guid}", HandleGetReleasePackage)
            .WithDisplayName("Get Metadata Release Package")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<MetadataReleasePackage>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/release-packages/{packageId:guid}/gitops-manifest", HandleGetGitOpsManifest)
            .WithDisplayName("Get Metadata Release GitOps Manifest")
            .WithSummary("Exports a persisted release package as a GitOps-safe JSON manifest.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<GitOpsMetadataReleaseManifest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleGetInventory(
        string environment,
        [FromServices] IMetadataReleaseService releaseService,
        HttpContext context,
        [FromQuery] string? artifactKind = null,
        [FromQuery] string? resourceType = null)
    {
        var artifactKindValid = TryParseArtifactKind(artifactKind, out var artifactKindFilter, out var artifactKindError);
        var resourceTypeValid = TryParseResourceType(resourceType, out var resourceTypeFilter, out var resourceTypeError);
        if (!artifactKindValid || !resourceTypeValid)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                artifactKindError ?? resourceTypeError ?? "Invalid inventory filter.");
        }

        try
        {
            var response = await releaseService.GetSemanticInventoryAsync(
                environment,
                new MetadataSemanticInventoryFilter
                {
                    ArtifactKind = artifactKindFilter,
                    ResourceType = resourceTypeFilter,
                },
                context.RequestAborted).ConfigureAwait(false);

            return response is null
                ? ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    $"Metadata v2 environment '{environment}' does not have an active revision.")
                : Results.Json(response, MetadataReleaseJsonContext.Default.MetadataSemanticInventoryResponse);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<IResult> HandleQueryEnvironmentBindings(
        [FromBody] MetadataEnvironmentBindingsRequest request,
        [FromServices] IMetadataReleaseService releaseService,
        HttpContext context)
    {
        try
        {
            var response = await releaseService.GetEnvironmentBindingsAsync(request, context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(response, MetadataReleaseJsonContext.Default.MetadataEnvironmentBindingsResponse);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<IResult> HandleCreateReleasePackage(
        [FromBody] CreateMetadataReleasePackageRequest request,
        [FromServices] IMetadataReleaseService releaseService,
        HttpContext context)
    {
        try
        {
            var actor = ConsolePrincipal.ResolveActorId(context.User) ?? "system";
            var package = await releaseService.CreateReleasePackageAsync(request, actor, context.RequestAborted)
                .ConfigureAwait(false);
            return Results.Json(
                package,
                MetadataReleaseJsonContext.Default.MetadataReleasePackage,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (InvalidOperationException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                "Metadata release package conflicts with an existing package.");
        }
        catch (Exception)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Metadata release package could not be created.");
        }
    }

    private static async Task<IResult> HandleGetReleasePackage(
        Guid packageId,
        [FromServices] IMetadataReleaseService releaseService,
        HttpContext context)
    {
        try
        {
            var package = await releaseService.GetReleasePackageAsync(packageId, context.RequestAborted)
                .ConfigureAwait(false);
            return package is null
                ? ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    $"Metadata release package '{packageId}' was not found.")
                : Results.Json(package, MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<IResult> HandleGetGitOpsManifest(
        Guid packageId,
        [FromServices] IMetadataReleaseService releaseService,
        HttpContext context)
    {
        try
        {
            var manifest = await releaseService.GetGitOpsManifestAsync(packageId, context.RequestAborted)
                .ConfigureAwait(false);
            return manifest is null
                ? ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    $"Metadata release package '{packageId}' was not found.")
                : Results.Json(manifest, MetadataReleaseJsonContext.Default.GitOpsMetadataReleaseManifest);
        }
        catch (ArgumentException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static bool TryParseArtifactKind(
        string? raw,
        out MetadataSemanticArtifactKind? value,
        out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        value = raw.Trim().ToLowerInvariant() switch
        {
            "resource" => MetadataSemanticArtifactKind.Resource,
            "service" => MetadataSemanticArtifactKind.Service,
            "publication" => MetadataSemanticArtifactKind.Publication,
            "field" => MetadataSemanticArtifactKind.Field,
            "catalog" => MetadataSemanticArtifactKind.Catalog,
            "policy" => MetadataSemanticArtifactKind.Policy,
            "role" => MetadataSemanticArtifactKind.Role,
            _ => null,
        };

        error = value is null ? $"Unsupported artifactKind filter '{raw}'." : null;
        return value is not null;
    }

    private static bool TryParseResourceType(
        string? raw,
        out MetadataV2ResourceType? value,
        out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        value = raw.Trim().ToLowerInvariant() switch
        {
            "feature-dataset" or "featuredataset" => MetadataV2ResourceType.FeatureDataset,
            "raster-dataset" or "rasterdataset" => MetadataV2ResourceType.RasterDataset,
            "table" => MetadataV2ResourceType.Table,
            "tile-dataset" or "tiledataset" => MetadataV2ResourceType.TileDataset,
            "process" => MetadataV2ResourceType.Process,
            "style" => MetadataV2ResourceType.Style,
            "document" => MetadataV2ResourceType.Document,
            "external-resource" or "externalresource" => MetadataV2ResourceType.ExternalResource,
            "map" => MetadataV2ResourceType.Map,
            "dashboard" => MetadataV2ResourceType.Dashboard,
            "form" => MetadataV2ResourceType.Form,
            "app" => MetadataV2ResourceType.App,
            "workflow" => MetadataV2ResourceType.Workflow,
            "geoprocessing-service" or "geoprocessingservice" => MetadataV2ResourceType.GeoprocessingService,
            "etl-pipeline" or "etlpipeline" => MetadataV2ResourceType.EtlPipeline,
            _ => null,
        };

        error = value is null ? $"Unsupported resourceType filter '{raw}'." : null;
        return value is not null;
    }
}
