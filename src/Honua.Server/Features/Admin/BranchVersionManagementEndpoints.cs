// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin/operator endpoints for registering and listing named branch versions used by
/// branch-versioned editing (#1272).
/// </summary>
/// <remarks>
/// <para>
/// A branch version isolates edits and reads for a single feature service layer from the
/// implicit DEFAULT version. Operators register a named version here; GeoServices clients
/// then target it through the <c>gdbVersion</c> parameter on FeatureServer query and
/// applyEdits requests. Branch feature rows live in the shared feature store under a distinct
/// synthetic storage layer id, so they are isolated from DEFAULT and are tracked and
/// synchronised by the existing incremental replication pipeline without branch-specific code.
/// </para>
/// <para>
/// Branch-versioned editing requires a writable feature provider (PostGIS). Read-only
/// analytics providers reject version creation with a not-supported denial.
/// </para>
/// </remarks>
internal static class BranchVersionManagementEndpoints
{
    /// <summary>
    /// Maps the admin branch-version management endpoints onto the admin services API group.
    /// </summary>
    public static void MapBranchVersionManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/services/{serviceId}/versions")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "BranchVersions")
            .RequireAdminAuthorization();

        _ = group.MapGet("/", HandleListVersions)
            .WithName("ListAdminBranchVersions")
            .WithSummary("List named branch versions registered against a feature service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPost("/", HandleCreateVersion)
            .WithName("CreateAdminBranchVersion")
            .WithSummary("Register a named branch version for branch-versioned editing")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleListVersions(
        string serviceId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IBranchVersionStore branchVersionStore,
        CancellationToken cancellationToken)
    {
        var serviceProblem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (serviceProblem != null)
        {
            return serviceProblem;
        }

        var versions = await branchVersionStore.ListVersionsAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var response = new BranchVersionListResponse
        {
            ServiceId = serviceId,
            Versions = versions.Select(ToResponse).ToArray(),
        };

        return Results.Json(
            ApiResponse<BranchVersionListResponse>.CreateSuccess(response),
            BranchVersionJsonContext.Default.ApiResponseBranchVersionListResponse);
    }

    private static async Task<IResult> HandleCreateVersion(
        string serviceId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IBranchVersionStore branchVersionStore,
        CancellationToken cancellationToken)
    {
        BranchVersionCreateRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                    BranchVersionJsonContext.Default.BranchVersionCreateRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid branch version request body.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.VersionName))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "A non-empty 'versionName' is required.");
        }

        if (IBranchVersionStore.IsDefaultVersion(request.VersionName))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "'DEFAULT' is a reserved branch version name and is always available.");
        }

        var layerResult = await resourceValidator
            .ValidateServiceLayerV2Async(serviceId, request.LayerId, cancellationToken)
            .ConfigureAwait(false);
        if (!layerResult.IsValid)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                statusCode,
                layerResult.ErrorMessage ?? $"Layer '{request.LayerId}' not found for service '{serviceId}'.");
        }

        var triple = layerResult.Resource;
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var baseLayerId = snapshot.ResolveStorageLayerId(triple.Publication)
            ?? snapshot.ResolveStorageLayerId(triple.Resource)
            ?? triple.Publication.LayerIndex;
        if (baseLayerId is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"Layer '{request.LayerId}' is not bound to feature storage and cannot be branch-versioned.");
        }

        try
        {
            var created = await branchVersionStore
                .CreateVersionAsync(serviceId, request.VersionName, baseLayerId.Value, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(
                ApiResponse<BranchVersionResponse>.CreateSuccess(ToResponse(created)),
                BranchVersionJsonContext.Default.ApiResponseBranchVersionResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (NotSupportedException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Branch-versioned editing requires a writable feature provider (PostGIS).");
        }
    }

    private static async Task<IResult?> ValidateServiceAsync(
        string serviceId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var serviceResult = await resourceValidator.ValidateServiceV2Async(serviceId, cancellationToken)
            .ConfigureAwait(false);
        if (serviceResult.IsValid && serviceResult.Resource != null)
        {
            return null;
        }

        var statusCode = serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status404NotFound;
        var message = serviceResult.ErrorMessage ?? $"Service '{serviceId}' not found.";
        return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message);
    }

    private static BranchVersionResponse ToResponse(BranchVersion version) => new()
    {
        ServiceId = version.ServiceId,
        VersionName = version.VersionName,
        BaseLayerId = version.BaseLayerId,
        BranchLayerId = version.BranchLayerId,
        CreatedAt = version.CreatedAt,
    };
}
