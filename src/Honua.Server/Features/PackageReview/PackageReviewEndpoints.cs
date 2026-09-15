// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Console;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.PackageReview;

/// <summary>
/// Admin endpoints for package validation and read-only preview planning.
/// </summary>
internal static partial class PackageReviewEndpoints
{
    internal sealed class PackageReviewEndpointsLog;

    /// <summary>
    /// Maps package-review endpoints.
    /// </summary>
    public static void MapPackageReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/packages")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Packages", "Validation")
            .RequireAdminAuthorization();

        group.MapPost("/validate", HandleValidate)
            .WithDisplayName("Validate Package")
            .WithSummary("Validate a package using the shared package-review contract.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<PackageReviewResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/preview", HandlePreview)
            .WithDisplayName("Preview Package")
            .WithSummary("Validate a package and return a read-only preview plan.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<PackageReviewResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/", HandlePublishMapPackage)
            .WithDisplayName("Publish Map Package")
            .WithSummary("Persist a map package as an immutable Studio version and propose its publication for separate-principal approval.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Accepts<MapPackagePublishRequest>("application/json")
            .Produces<MapPackagePublishResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static Task<IResult> HandleValidate(
        PackageReviewRequest request,
        HttpContext context,
        [FromServices] IPackageReviewService reviewService,
        [FromServices] ILogger<PackageReviewEndpointsLog> logger)
        => HandleReview(request.WithPreviewPlan(false), context, reviewService, logger);

    private static Task<IResult> HandlePreview(
        PackageReviewRequest request,
        HttpContext context,
        [FromServices] IPackageReviewService reviewService,
        [FromServices] ILogger<PackageReviewEndpointsLog> logger)
        => HandleReview(request.WithPreviewPlan(true), context, reviewService, logger);

    private static async Task<IResult> HandleReview(
        PackageReviewRequest request,
        HttpContext context,
        IPackageReviewService reviewService,
        ILogger<PackageReviewEndpointsLog> logger)
    {
        try
        {
            var reviewContext = PackageReviewContextFactory.FromHttpContext(context);
            var response = await reviewService.ReviewAsync(
                request,
                reviewContext,
                context.RequestAborted).ConfigureAwait(false);

            PackageReviewLog.PackageReviewCompleted(
                logger,
                response.PackageFamily,
                response.Status,
                response.Findings.Count);

            return Results.Json(
                ApiResponse<PackageReviewResponse>.CreateSuccess(response),
                PackageReviewJsonContext.Default.ApiResponsePackageReviewResponse);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // admin problem-details response below.
            PackageReviewLog.PackageReviewFailed(logger, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Package review failed",
                "An internal error occurred while reviewing the package.");
        }
    }

    private static async Task<IResult> HandlePublishMapPackage(
        HttpContext context,
        [FromServices] IStudioDraftMutationRuntime mutationRuntime)
    {
        // honua-server#4906: the body is read here rather than bound by the framework. Framework
        // binding refused every package the installed CLI sends with an empty 400: the
        // honua_map_package.v1 document makes status/createdAt optional where MapPackage marks
        // them required, and the combined HTTP resolver reads PackageStatus without the
        // package contexts' string-enum converter.
        var read = await MapPackagePublishRequestReader.ReadAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (read.MissingPackage)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status400BadRequest, "Map package is required", "package.mapPackageId is required.");
        }

        if (read.Request is not { } request)
        {
            return ProblemDetailsHelpers.CreateValidationProblem(context, StatusCodes.Status400BadRequest, read.Errors);
        }

        var actor = ConsolePrincipal.ResolveActorId(context.User);
        var mutation = BuildMutationContext(context, actor);
        var packageJson = JsonSerializer.SerializeToElement(request.Package, PackagingJsonContext.Default.MapPackage);
        var draftReceipt = await mutationRuntime.CreateAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = request.Package.MapPackageId,
            WorkspaceId = request.WorkspaceId,
            OwnerId = actor,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "studio_map.v1",
                Format = request.Package.Format,
                Body = packageJson,
                PublicationIntent = request.Intent
            },
            ActorId = actor
        }, WithStep(mutation, "draft"), context.RequestAborted).ConfigureAwait(false);
        SetOperationHeaders(context, draftReceipt.Operation);
        if (draftReceipt.Operation.Status != OperationHandleStatus.Completed || draftReceipt.Value is not { } draft)
        {
            return MutationRefused(context, draftReceipt.Operation, "Map package draft could not be created");
        }

        var versionReceipt = await mutationRuntime.SaveVersionAsync(
            draft.DraftId, draft.Generation, request.Message, actor, WithStep(mutation, "version"), context.RequestAborted)
            .ConfigureAwait(false);
        SetOperationHeaders(context, versionReceipt.Operation);
        if (versionReceipt.Operation.Status != OperationHandleStatus.Completed || versionReceipt.Value is not { } version)
        {
            return MutationRefused(context, versionReceipt.Operation, "Map package could not be versioned");
        }

        // Publication is proposed, never executed, on this route: like honua_studio_propose_publication,
        // the published pointer only moves after a separate authorized principal approves the proposal.
        var proposalReceipt = await mutationRuntime.CreatePublicationRequestAsync(
            version.ItemId,
            version.VersionId,
            version.ContentHash,
            request.Intent,
            request.WarningAcknowledgement,
            actor,
            WithStep(mutation, "publication") with { ActionDiscriminator = BuiltInGuardrailActions.StudioPublicationProposal },
            context.RequestAborted).ConfigureAwait(false);
        var operation = proposalReceipt.Operation;
        SetOperationHeaders(context, operation);
        if (operation.Status != OperationHandleStatus.RequiresApproval || string.IsNullOrWhiteSpace(operation.ProposalId))
        {
            return MutationRefused(context, operation, "Map package publication was not proposed");
        }

        return Results.Json(new MapPackagePublishResponse
        {
            PackageId = request.Package.MapPackageId,
            ItemId = version.ItemId,
            VersionId = version.VersionId,
            ContentHash = version.ContentHash,
            Package = request.Package with { Status = PackageStatus.Ready, UpdatedAt = version.CreatedAt },
            PublicationStatus = "AwaitingApproval",
            ProposalId = operation.ProposalId,
            OperationInstanceId = operation.OperationInstanceId,
            AuditId = operation.AuditId,
            CorrelationId = operation.CorrelationId
        }, PackageReviewJsonContext.Default.MapPackagePublishResponse, statusCode: StatusCodes.Status202Accepted);
    }

    private static StudioDraftMutationContext BuildMutationContext(HttpContext context, string? actorId) => new()
    {
        PrincipalId = actorId,
        TenantId = context.RequestServices.GetService<ITenantContext>()?.TenantId,
        SchemaName = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
        CorrelationId = context.TraceIdentifier,
        IdempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
        AuthorizationOutcome = "authorized",
        Roles = context.User.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray(),
        ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(context.User),
        RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(context.User)
            .OrderBy(static scope => scope, StringComparer.Ordinal)
            .ToArray(),
    };

    // One publish request drives three runtime operations; the runtime scopes an idempotency key
    // by tenant and principal only, so each step gets its own derived key to keep them distinct.
    private static StudioDraftMutationContext WithStep(StudioDraftMutationContext context, string step)
        => string.IsNullOrWhiteSpace(context.IdempotencyKey)
            ? context
            : context with { IdempotencyKey = $"{context.IdempotencyKey}:map-package-publish:{step}" };

    private static void SetOperationHeaders(HttpContext context, OperationHandle operation)
    {
        context.Response.Headers["X-Honua-Operation-Instance-Id"] = operation.OperationInstanceId;
        context.Response.Headers["X-Honua-Operation-Correlation-Id"] = operation.CorrelationId;
        if (!string.IsNullOrWhiteSpace(operation.AuditId))
        {
            context.Response.Headers["X-Honua-Operation-Audit-Id"] = operation.AuditId;
        }
    }

    private static IResult MutationRefused(HttpContext context, OperationHandle operation, string title)
    {
        var detail = operation.Reason ?? "The Studio operation did not complete.";
        var statusCode = operation.Result?.Details.TryGetValue("errorKind", out var errorKind) == true
            ? errorKind switch
            {
                "argument" => StatusCodes.Status400BadRequest,
                "not-found" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status409Conflict,
            }
            : operation.Status switch
            {
                OperationHandleStatus.Denied => StatusCodes.Status403Forbidden,
                OperationHandleStatus.Failed => StatusCodes.Status500InternalServerError,
                _ => StatusCodes.Status409Conflict,
            };
        return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, title, detail);
    }

    internal sealed record MapPackagePublishRequest
    {
        public required MapPackage Package { get; init; }
        public string? MapId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? Message { get; init; }
        public string? WarningAcknowledgement { get; init; }
        public StudioPublicationIntent? Intent { get; init; }
    }

    /// <summary>
    /// Accepted publication proposal for a saved map package version. Nothing is published until
    /// a separate principal approves <see cref="ProposalId"/>.
    /// </summary>
    internal sealed record MapPackagePublishResponse
    {
        public required string PackageId { get; init; }
        public required Guid ItemId { get; init; }
        public required Guid VersionId { get; init; }
        public required string ContentHash { get; init; }
        public required MapPackage Package { get; init; }
        public required string PublicationStatus { get; init; }
        public required string ProposalId { get; init; }
        public required string OperationInstanceId { get; init; }
        public string? AuditId { get; init; }
        public required string CorrelationId { get; init; }
    }

}
