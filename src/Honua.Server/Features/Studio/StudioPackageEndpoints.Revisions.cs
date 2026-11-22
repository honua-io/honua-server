// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Exceptions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Studio.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Studio;

internal static partial class StudioPackageEndpoints
{
    private const string DraftResourceType = "studio-package-draft";

    private static void MapStudioDraftRevisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/studio/drafts/{draftId:guid}/revisions")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Studio")
            .RequireStudioLifecycleAuthorization();

        group.MapGet(string.Empty, HandleListDraftRevisions)
            .WithDisplayName("List Studio Draft Revisions")
            .WithSummary("Lists retained Studio draft revision summaries in descending generation order.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{generation:long}", HandleGetDraftRevision)
            .WithDisplayName("Get Studio Draft Revision")
            .WithSummary("Returns one retained Studio draft revision envelope.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/{generation:long}/restore", HandleRestoreDraftRevision)
            .WithDisplayName("Restore Studio Draft Revision")
            .WithSummary("Restores a retained Studio draft revision through the canonical mutation runtime.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleListDraftRevisions(
        [AsParameters] StudioDraftRevisionScope scope,
        [FromQuery] long? beforeGeneration = null,
        [FromQuery] int limit = 25)
    {
        if (beforeGeneration is <= 0)
        {
            return BadRequest(scope.Context, "beforeGeneration must be a positive integer.");
        }

        try
        {
            var (draft, failure) = await LoadAuthorizedDraftAsync(scope, StudioAuthorizationOperation.ReadDraft)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            var items = await scope.Service.ListDraftRevisionsAsync(
                new StudioDraftRevisionQuery(scope.DraftId, ClampListLimit(limit), beforeGeneration),
                scope.Context.RequestAborted).ConfigureAwait(false);
            var response = new StudioDraftRevisionListResponse
            {
                DraftId = draft!.DraftId,
                CurrentGeneration = draft.Generation,
                Items = items,
            };
            return Results.Json(
                ApiResponse<StudioDraftRevisionListResponse>.CreateSuccess(response),
                StudioApiJsonContext.Default.ApiResponseStudioDraftRevisionListResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(scope.Logger, "draft.revision.list", ex);
            return ServerError(scope.Context, "Studio package draft revisions could not be listed.");
        }
    }

    private static async Task<IResult> HandleGetDraftRevision(
        [AsParameters] StudioDraftRevisionScope scope,
        long generation)
    {
        if (generation < 1)
        {
            return BadRequest(scope.Context, "generation must be a positive integer.");
        }

        try
        {
            var (_, failure) = await LoadAuthorizedDraftAsync(scope, StudioAuthorizationOperation.ReadDraft)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            var revision = await scope.Service.GetDraftRevisionAsync(scope.DraftId, generation, scope.Context.RequestAborted)
                .ConfigureAwait(false);
            return revision is null
                ? NotFound(scope.Context, "Studio package draft revision was not found.")
                : Results.Json(
                    ApiResponse<StudioDraftRevision>.CreateSuccess(revision),
                    StudioApiJsonContext.Default.ApiResponseStudioDraftRevision);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(scope.Logger, "draft.revision.get", ex);
            return ServerError(scope.Context, "Studio package draft revision could not be loaded.");
        }
    }

    private static async Task<IResult> HandleRestoreDraftRevision(
        [AsParameters] StudioDraftRevisionScope scope,
        long generation,
        RestoreStudioDraftRevisionRequest request)
    {
        if (RestoreRequestError(generation, request) is { } validationError)
        {
            return BadRequest(scope.Context, validationError);
        }

        try
        {
            return await RestoreAuthorizedDraftRevisionAsync(scope, generation, request.ExpectedGeneration)
                .ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException ex)
        {
            return CapabilityUnavailable(scope.Context, ex);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(scope.Context, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(scope.Context, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(scope.Context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioEndpointsLog.EndpointFailed(scope.Logger, "draft.revision.restore", ex);
            return ServerError(scope.Context, "Studio package draft revision could not be restored.");
        }
    }

    private static string? RestoreRequestError(long generation, RestoreStudioDraftRevisionRequest request)
    {
        if (generation < 1)
        {
            return "generation must be a positive integer.";
        }

        return TryValidateRequest(request, out var validationError) ? null : validationError;
    }

    private static async Task<IResult> RestoreAuthorizedDraftRevisionAsync(
        StudioDraftRevisionScope scope,
        long generation,
        long expectedGeneration)
    {
        var (_, failure) = await LoadAuthorizedDraftAsync(scope, StudioAuthorizationOperation.UpdateDraft)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var context = scope.Context;
        var mutationRuntime = context.RequestServices.GetRequiredService<IStudioDraftMutationRuntime>();
        var actor = scope.Authorization.ResolveCallerId(context.User);
        var command = new RestoreStudioDraftRevisionCommand
        {
            TargetGeneration = generation,
            ExpectedGeneration = expectedGeneration,
            ActorId = actor,
        };
        var restore = new StudioDraftRevisionRestore(scope.DraftId, command);
        var receipt = await mutationRuntime.RestoreRevisionAsync(
            restore,
            BuildMutationContext(context, actor) with { IdempotencyRequestHash = RestoreRequestHash(restore) },
            context.RequestAborted).ConfigureAwait(false);
        SetOperationHeaders(context, receipt.Operation);
        if (receipt.Operation.Status != OperationHandleStatus.Completed)
        {
            return MutationDecision(context, receipt.Operation);
        }

        var restored = receipt.Value
            ?? throw new InvalidOperationException("Completed Studio draft revision restore has no durable result projection.");
        StudioEndpointsLog.DraftUpdated(scope.Logger, restored.DraftId, restored.Generation);
        return Results.Json(
            ApiResponse<StudioPackageDraft>.CreateSuccess(restored),
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
    }

    private static string RestoreRequestHash(StudioDraftRevisionRestore restore)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{restore.DraftId:D}|{restore.Command.TargetGeneration}|{restore.Command.ExpectedGeneration}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task<(StudioPackageDraft? Draft, IResult? Failure)> LoadAuthorizedDraftAsync(
        StudioDraftRevisionScope scope,
        StudioAuthorizationOperation operation)
    {
        var draftId = scope.DraftId.ToString("D");
        var existing = await scope.Service.GetDraftAsync(scope.DraftId, scope.Context.RequestAborted).ConfigureAwait(false);
        if (existing is null)
        {
            var notFound = await NotFoundAfterAuthorizationAsync(
                scope.Authorization, scope.Context, operation,
                resourceType: DraftResourceType, resourceId: draftId,
                "Studio package draft was not found.").ConfigureAwait(false);
            return (null, notFound);
        }

        var authResult = await EnsureAuthorizedAsync(
            scope.Authorization, scope.Context, operation, existing.OwnerId, existing.TenantId,
            resourceType: DraftResourceType, resourceId: draftId).ConfigureAwait(false);
        return authResult is not null ? (null, authResult) : (existing, null);
    }
}

internal sealed class StudioDraftRevisionScope
{
    [FromRoute(Name = "draftId")]
    public Guid DraftId { get; init; }

    [FromServices]
    public IStudioPackageLifecycleService Service { get; init; } = null!;

    [FromServices]
    public StudioEndpointAuthorization Authorization { get; init; } = null!;

    [FromServices]
    public ILogger<StudioPackageEndpoints.StudioPackageEndpointsMarker> Logger { get; init; } = null!;

    public HttpContext Context { get; init; } = null!;
}
