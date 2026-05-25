// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.WorkflowPackages;

internal static class WorkflowPackageEndpoints
{
    public static void MapWorkflowPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console", "Workflow Packages")
            .RequireAdminAuthorization();

        group.MapGet("/workflow-node-registry", HandleGetNodeRegistry)
            .WithName("GetWorkflowNodeRegistry")
            .WithSummary("Get the server-owned workflow node registry")
            .Produces<ApiResponse<WorkflowNodeRegistrySnapshot>>();

        group.MapGet("/workflow-node-registry/{nodeTypeId}", HandleGetNode)
            .WithName("GetWorkflowNodeDefinition")
            .WithSummary("Get a workflow node definition")
            .Produces<ApiResponse<WorkflowNodeDefinition>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workflow-packages", HandleListPackages)
            .WithName("ListWorkflowPackages")
            .WithSummary("List workflow package drafts")
            .Produces<ApiResponse<WorkflowPackageListResponse>>();

        group.MapPost("/workflow-packages", HandleSavePackage)
            .WithName("CreateWorkflowPackage")
            .WithSummary("Create or replace a workflow package draft")
            .Accepts<SaveWorkflowPackageRequest>("application/json")
            .Produces<ApiResponse<WorkflowPackage>>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/workflow-packages/{packageId}", HandleGetPackage)
            .WithName("GetWorkflowPackage")
            .WithSummary("Get a workflow package draft")
            .Produces<ApiResponse<WorkflowPackage>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/workflow-packages/{packageId}", HandleUpdatePackage)
            .WithName("UpdateWorkflowPackage")
            .WithSummary("Replace a workflow package draft")
            .Accepts<SaveWorkflowPackageRequest>("application/json")
            .Produces<ApiResponse<WorkflowPackage>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workflow-packages/{packageId}/versions", HandleListVersions)
            .WithName("ListWorkflowPackageVersions")
            .WithSummary("List immutable workflow package versions")
            .Produces<ApiResponse<WorkflowPackageVersionListResponse>>();

        group.MapPost("/workflow-packages/{packageId}/versions", HandleCreateVersion)
            .WithName("CreateWorkflowPackageVersion")
            .WithSummary("Create an immutable workflow package version")
            .Produces<ApiResponse<WorkflowPackageVersion>>(StatusCodes.Status201Created)
            .Produces<ApiResponse<WorkflowPackageValidationResult>>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workflow-packages/{packageId}/versions/{packageVersion:int}", HandleGetVersion)
            .WithName("GetWorkflowPackageVersion")
            .WithSummary("Get an immutable workflow package version")
            .Produces<ApiResponse<WorkflowPackageVersion>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/workflow-packages/{packageId}/versions/{packageVersion:int}/validate", HandleValidateVersion)
            .WithName("ValidateWorkflowPackageVersion")
            .WithSummary("Validate a workflow package version")
            .Produces<ApiResponse<WorkflowPackageValidationResult>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/workflow-packages/{packageId}/versions/{packageVersion:int}/dry-run", HandleDryRunVersion)
            .WithName("DryRunWorkflowPackageVersion")
            .WithSummary("Dry-run a workflow package version")
            .Produces<ApiResponse<WorkflowDryRunResult>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/workflow-packages/{packageId}/versions/{packageVersion:int}/publish", HandlePublishVersion)
            .WithName("PublishWorkflowPackageVersion")
            .WithSummary("Publish a workflow package version to a job, schedule, or process endpoint target")
            .Accepts<PublishWorkflowPackageRequest>("application/json")
            .Produces<ApiResponse<WorkflowPublication>>()
            .Produces<ApiResponse<WorkflowPackageValidationResult>>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workflow-publications", HandleListPublications)
            .WithName("ListWorkflowPublications")
            .WithSummary("List workflow package publications")
            .Produces<ApiResponse<WorkflowPublicationListResponse>>();

        group.MapPost("/workflow-publications/{publicationId}/runs", HandleRunPublication)
            .WithName("RunWorkflowPublication")
            .WithSummary("Start a run from a workflow package publication")
            .Accepts<RunWorkflowPublicationRequest>("application/json")
            .Produces<ApiResponse<WorkflowPublicationRunResult>>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> HandleGetNodeRegistry(
        HttpContext context,
        IWorkflowNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var snapshot = await registry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = $"\"{snapshot.RegistryVersion}\"";
        return TypedResults.Ok(ApiResponse<WorkflowNodeRegistrySnapshot>.CreateSuccess(snapshot));
    }

    private static async Task<IResult> HandleGetNode(
        HttpContext context,
        string nodeTypeId,
        IWorkflowNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var node = await registry.GetNodeAsync(nodeTypeId, cancellationToken).ConfigureAwait(false);
        return node == null
            ? NotFound(context, $"Workflow node type '{nodeTypeId}' was not found.")
            : TypedResults.Ok(ApiResponse<WorkflowNodeDefinition>.CreateSuccess(node));
    }

    private static async Task<IResult> HandleListPackages(
        HttpContext context,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var packages = await service.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<WorkflowPackageListResponse>.CreateSuccess(new WorkflowPackageListResponse
        {
            Items = packages.ToArray()
        }));
    }

    private static async Task<IResult> HandleSavePackage(
        HttpContext context,
        SaveWorkflowPackageRequest request,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        try
        {
            var package = await service.SavePackageAsync(request, context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Created(
                $"/api/v1/console/workflow-packages/{package.PackageId}",
                ApiResponse<WorkflowPackage>.CreateSuccess(package));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
    }

    private static async Task<IResult> HandleGetPackage(
        HttpContext context,
        string packageId,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var package = await service.GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
        return package == null
            ? NotFound(context, $"Workflow package '{packageId}' was not found.")
            : TypedResults.Ok(ApiResponse<WorkflowPackage>.CreateSuccess(package));
    }

    private static async Task<IResult> HandleUpdatePackage(
        HttpContext context,
        string packageId,
        SaveWorkflowPackageRequest request,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        if (!string.IsNullOrWhiteSpace(request.PackageId) &&
            !string.Equals(packageId, request.PackageId, StringComparison.Ordinal))
        {
            return BadRequest(context, "Route packageId must match request packageId.");
        }

        var existing = await service.GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            return NotFound(context, $"Workflow package '{packageId}' was not found.");
        }

        try
        {
            var package = await service
                .SavePackageAsync(request with { PackageId = packageId }, context.User, cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<WorkflowPackage>.CreateSuccess(package));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
    }

    private static async Task<IResult> HandleListVersions(
        HttpContext context,
        string packageId,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var versions = await service.ListVersionsAsync(packageId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<WorkflowPackageVersionListResponse>.CreateSuccess(new WorkflowPackageVersionListResponse
        {
            Items = versions.ToArray()
        }));
    }

    private static async Task<IResult> HandleCreateVersion(
        HttpContext context,
        string packageId,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
        => await ExecuteVersionMutationAsync(
            context,
            async () =>
            {
                var version = await service.CreateVersionAsync(packageId, context.User, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created(
                    $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}",
                    ApiResponse<WorkflowPackageVersion>.CreateSuccess(version));
            }).ConfigureAwait(false);

    private static async Task<IResult> HandleGetVersion(
        HttpContext context,
        string packageId,
        int packageVersion,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var version = await service.GetVersionAsync(packageId, packageVersion, cancellationToken).ConfigureAwait(false);
        return version == null
            ? NotFound(context, $"Workflow package '{packageId}' version {packageVersion} was not found.")
            : TypedResults.Ok(ApiResponse<WorkflowPackageVersion>.CreateSuccess(version));
    }

    private static async Task<IResult> HandleValidateVersion(
        HttpContext context,
        string packageId,
        int packageVersion,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
        => await ExecuteVersionMutationAsync(
            context,
            async () =>
            {
                var result = await service.ValidateVersionAsync(packageId, packageVersion, context.User, cancellationToken)
                    .ConfigureAwait(false);
                return TypedResults.Ok(ApiResponse<WorkflowPackageValidationResult>.CreateSuccess(result));
            }).ConfigureAwait(false);

    private static async Task<IResult> HandleDryRunVersion(
        HttpContext context,
        string packageId,
        int packageVersion,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
        => await ExecuteVersionMutationAsync(
            context,
            async () =>
            {
                var result = await service.DryRunVersionAsync(packageId, packageVersion, context.User, cancellationToken)
                    .ConfigureAwait(false);
                return TypedResults.Ok(ApiResponse<WorkflowDryRunResult>.CreateSuccess(result));
            }).ConfigureAwait(false);

    private static async Task<IResult> HandlePublishVersion(
        HttpContext context,
        string packageId,
        int packageVersion,
        PublishWorkflowPackageRequest request,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
        => await ExecuteVersionMutationAsync(
            context,
            async () =>
            {
                var publication = await service
                    .PublishVersionAsync(packageId, packageVersion, request, context.User, cancellationToken)
                    .ConfigureAwait(false);
                return TypedResults.Ok(ApiResponse<WorkflowPublication>.CreateSuccess(publication));
            }).ConfigureAwait(false);

    private static async Task<IResult> HandleListPublications(
        HttpContext context,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var publications = await service.ListPublicationsAsync(null, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<WorkflowPublicationListResponse>.CreateSuccess(new WorkflowPublicationListResponse
        {
            Items = publications.ToArray()
        }));
    }

    private static async Task<IResult> HandleRunPublication(
        HttpContext context,
        string publicationId,
        RunWorkflowPublicationRequest? request,
        WorkflowPackageService service,
        CancellationToken cancellationToken)
        => await ExecuteVersionMutationAsync(
            context,
            async () =>
            {
                var result = await service
                    .RunPublicationAsync(
                        publicationId,
                        request ?? new RunWorkflowPublicationRequest(),
                        context.User,
                        cancellationToken)
                    .ConfigureAwait(false);
                return TypedResults.Created(
                    result.JobId == null
                        ? $"/api/v1/admin/operations/{result.WorkflowRunId}"
                        : $"/api/v1/admin/jobs/{result.JobId}",
                    ApiResponse<WorkflowPublicationRunResult>.CreateSuccess(result));
            }).ConfigureAwait(false);

    private static async Task<IResult> ExecuteVersionMutationAsync(
        HttpContext context,
        Func<Task<IResult>> action)
    {
        SetNoStore(context);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (WorkflowPackageValidationException ex)
        {
            return Results.Json(
                ApiResponse<WorkflowPackageValidationResult>.Failure("Workflow package validation failed.", ex.Result),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(context, ex.Message);
        }
        catch (GeoprocessingAuthorizationException ex)
        {
            var status = ex.RequiresAuthentication
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                status,
                ProblemDetailsHelpers.GetTitle(status),
                ex.Message);
        }
        catch (GeoprocessingValidationException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (GeoprocessingStoreUnavailableException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                ex.Message);
        }
        catch (GeoprocessingAdmissionException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status429TooManyRequests,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status429TooManyRequests),
                ex.Message);
        }
        catch (GeoprocessingApprovalRequiredException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                ex.Message);
        }
    }

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
            detail);

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
            detail);

    private static void SetNoStore(HttpContext context)
        => context.Response.Headers.CacheControl = "no-store";
}
