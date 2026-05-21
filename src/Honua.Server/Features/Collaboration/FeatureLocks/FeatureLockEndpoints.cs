// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Collaboration.FeatureLocks;

internal static class FeatureLockEndpoints
{
    public static void MapFeatureLockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/saved-maps/{mapId}/collaboration/feature-locks")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Saved Maps", "Collaboration", "Feature Locks")
            // Mutation endpoints — require an authenticated principal up front so
            // unauthenticated requests are rejected at the middleware boundary,
            // before reaching the per-feature lock authorizer in the handler.
            .RequireAuthorization();

        group.MapPost("/claim", HandleClaim)
            .WithDisplayName("Claim Saved Map Feature Lock")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<FeatureLockClaimResponse>>()
            .Produces<ApiResponse<FeatureLockClaimResponse>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/renew", HandleRenew)
            .WithDisplayName("Renew Saved Map Feature Lock")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<FeatureLockRenewResponse>>()
            .Produces<ApiResponse<FeatureLockRenewResponse>>(StatusCodes.Status404NotFound)
            .Produces<ApiResponse<FeatureLockRenewResponse>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/release", HandleRelease)
            .WithDisplayName("Release Saved Map Feature Lock")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Produces<ApiResponse<FeatureLockReleaseResponse>>()
            .Produces<ApiResponse<FeatureLockReleaseResponse>>(StatusCodes.Status404NotFound)
            .Produces<ApiResponse<FeatureLockReleaseResponse>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> HandleClaim(
        string mapId,
        [FromBody] FeatureLockMutationRequest request,
        [FromServices] IFeatureLockService locks,
        [FromServices] IFeatureLockAuthorizer authorizer,
        HttpContext context)
    {
        var auth = await AuthorizeAsync(mapId, request, authorizer, context, "claim").ConfigureAwait(false);
        if (!auth.Authorized)
        {
            return CreateAuthorizationFailure(auth, context);
        }

        var result = await locks.ClaimAsync(
                request.ToFeatureRef(),
                request.ToHolder(),
                request.ToLeaseDuration(),
                auth.Access,
                context.RequestAborted)
            .ConfigureAwait(false);

        return result.Status switch
        {
            FeatureLockClaimStatus.HeldByOther => JsonClaim(
                ApiResponse<FeatureLockClaimResponse>.Failure("The feature is locked by another editor.", result),
                StatusCodes.Status409Conflict),
            FeatureLockClaimStatus.Denied => CreateForbidden(context, result.DenialReason),
            _ => JsonClaim(ApiResponse<FeatureLockClaimResponse>.CreateSuccess(result))
        };
    }

    private static async Task<IResult> HandleRenew(
        string mapId,
        [FromBody] FeatureLockMutationRequest request,
        [FromServices] IFeatureLockService locks,
        [FromServices] IFeatureLockAuthorizer authorizer,
        HttpContext context)
    {
        var auth = await AuthorizeAsync(mapId, request, authorizer, context, "renew").ConfigureAwait(false);
        if (!auth.Authorized)
        {
            return CreateAuthorizationFailure(auth, context);
        }

        var result = await locks.RenewAsync(
                request.ToFeatureRef(),
                request.ToHolder(),
                request.ToLeaseDuration(),
                auth.Access,
                context.RequestAborted)
            .ConfigureAwait(false);

        return result.Status switch
        {
            FeatureLockRenewStatus.NotFound => JsonRenew(
                ApiResponse<FeatureLockRenewResponse>.Failure("No active feature lock was found.", result),
                StatusCodes.Status404NotFound),
            FeatureLockRenewStatus.HeldByOther => JsonRenew(
                ApiResponse<FeatureLockRenewResponse>.Failure("The feature is locked by another editor.", result),
                StatusCodes.Status409Conflict),
            FeatureLockRenewStatus.Denied => CreateForbidden(context, result.DenialReason),
            _ => JsonRenew(ApiResponse<FeatureLockRenewResponse>.CreateSuccess(result))
        };
    }

    private static async Task<IResult> HandleRelease(
        string mapId,
        [FromBody] FeatureLockMutationRequest request,
        [FromServices] IFeatureLockService locks,
        [FromServices] IFeatureLockAuthorizer authorizer,
        HttpContext context)
    {
        var auth = await AuthorizeAsync(mapId, request, authorizer, context, "release").ConfigureAwait(false);
        if (!auth.Authorized)
        {
            return CreateAuthorizationFailure(auth, context);
        }

        var result = await locks.ReleaseAsync(
                request.ToFeatureRef(),
                request.ToHolder(),
                auth.Access,
                context.RequestAborted)
            .ConfigureAwait(false);

        return result.Status switch
        {
            FeatureLockReleaseStatus.NotFound => JsonRelease(
                ApiResponse<FeatureLockReleaseResponse>.Failure("No active feature lock was found.", result),
                StatusCodes.Status404NotFound),
            FeatureLockReleaseStatus.HeldByOther => JsonRelease(
                ApiResponse<FeatureLockReleaseResponse>.Failure("The feature is locked by another editor.", result),
                StatusCodes.Status409Conflict),
            FeatureLockReleaseStatus.Denied => CreateForbidden(context, result.DenialReason),
            _ => JsonRelease(ApiResponse<FeatureLockReleaseResponse>.CreateSuccess(result))
        };
    }

    private static ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
        string mapId,
        FeatureLockMutationRequest request,
        IFeatureLockAuthorizer authorizer,
        HttpContext context,
        string operation) =>
        authorizer.AuthorizeAsync(
            mapId,
            request.ToFeatureRef(),
            context.User,
            operation,
            context.RequestAborted);

    private static IResult CreateAuthorizationFailure(
        FeatureLockAuthorizationResult auth,
        HttpContext context)
    {
        return auth.Status == FeatureLockAuthorizationStatus.RequiresAuthentication
            ? StandardErrorHelpers.CreateUnauthorized(
                context,
                auth.Detail ?? "Authentication is required to mutate a feature lock.")
            : CreateForbidden(context, auth.Detail);
    }

    private static IResult CreateForbidden(HttpContext context, string? detail) =>
        StandardErrorHelpers.CreateForbidden(
            context,
            detail ?? "You are not allowed to mutate this feature lock.");

    private static IResult JsonClaim(
        ApiResponse<FeatureLockClaimResponse> response,
        int statusCode = StatusCodes.Status200OK) =>
        Results.Json(
            response,
            FeatureLockJsonContext.Default.ApiResponseFeatureLockClaimResponse,
            statusCode: statusCode);

    private static IResult JsonRenew(
        ApiResponse<FeatureLockRenewResponse> response,
        int statusCode = StatusCodes.Status200OK) =>
        Results.Json(
            response,
            FeatureLockJsonContext.Default.ApiResponseFeatureLockRenewResponse,
            statusCode: statusCode);

    private static IResult JsonRelease(
        ApiResponse<FeatureLockReleaseResponse> response,
        int statusCode = StatusCodes.Status200OK) =>
        Results.Json(
            response,
            FeatureLockJsonContext.Default.ApiResponseFeatureLockReleaseResponse,
            statusCode: statusCode);
}
