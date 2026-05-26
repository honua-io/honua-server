// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Authenticated Console Share access endpoints: share projection read, access
/// tier changes, dependency-closure preview, and public-link/embed token
/// lifecycle. All routes require admin authorization (the share facet is a
/// privileged management surface).
/// </summary>
internal static class ConsoleShareEndpoints
{
    private const int MaxClosureDepth = 10;
    private const int DefaultEmbedTtlSeconds = 3600;

    /// <summary>
    /// Log category for the Console Share access endpoints.
    /// </summary>
    internal sealed class ConsoleShareEndpointsLogCategory;

    /// <summary>
    /// Maps the authenticated Console Share endpoints into the supplied builder.
    /// </summary>
    public static void MapConsoleShareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/content/{id}/share")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleGetShare)
            .WithDisplayName("Get Console Share Projection")
            .WithSummary("Returns the server-owned Share access projection for a content item: access tier, public-link and embed state, open-data eligibility, caller permissions, and anonymous eligibility.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/access", HandleUpdateAccess)
            .WithDisplayName("Update Console Share Access Tier")
            .WithSummary("Changes the item's Share access tier. Public-link/public-indexed changes are dependency-closure validated and return 409 with the blocking dependencies unless allowDependencyConflicts is set.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapGet("/dependencies", HandleGetDependencies)
            .WithDisplayName("Preview Console Share Dependency Closure")
            .WithSummary("Evaluates whether the item's provenance closure is shareable by a target tier without committing the change.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/link", HandleListPublicLinks)
            .WithDisplayName("List Console Public-Link Tokens")
            .WithSummary("Lists public-link tokens for the item. Token values are never returned after minting.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/link", HandleMintPublicLink)
            .WithDisplayName("Mint Console Public-Link Token")
            .WithSummary("Mints a public-link token (returns the opaque value once). Requires the item's access tier to already be public-link or public-indexed.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapDelete("/link/{tokenId}", HandleExpirePublicLink)
            .WithDisplayName("Revoke Console Public-Link Token")
            .WithSummary("Revokes a public-link token by id. Subsequent anonymous resolution returns 404.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapPut("/embed", HandleSetEmbed)
            .WithDisplayName("Configure Console Embed")
            .WithSummary("Enables or disables embedding with an audience scope. Enabling is dependency-closure validated.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPost("/embed", HandleMintEmbedToken)
            .WithDisplayName("Mint Console Embed Token")
            .WithSummary("Mints an embed token (returns the opaque value once) with a TTL and audience. Requires embedding to be enabled.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleShareProjection>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleGetShare(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var projection = await BuildProjectionAsync(id, contentStore, shareStore, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            if (projection is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            return TypedResults.Ok(ApiResponse<ConsoleShareProjection>.CreateSuccess(projection));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.get", ex);
            return TypedResults.Problem(
                title: "Console share projection failed",
                detail: "An internal error occurred while resolving the Console share projection.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleShareProjection>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<ConsoleShareDependencyClosureResponse>>, ProblemHttpResult>> HandleUpdateAccess(
        string id,
        UpdateShareAccessRequest request,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleDependencyClosureValidator validator,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            if (!ConsoleEnumParser.IsDefined(request.AccessTier))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"accessTier '{(int)request.AccessTier}' is not a defined Console share access tier."));
            }

            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var currentState = await shareStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            var oldTier = ConsoleShareMapper.ResolveEffectiveTier(currentState, item);
            var targetTier = request.AccessTier;

            if (RequiresClosureCheck(targetTier))
            {
                var conflicts = await validator.ValidateAsync(id, targetTier, MaxClosureDepth, context.RequestAborted).ConfigureAwait(false);
                if (conflicts.Count > 0)
                {
                    if (!request.AllowDependencyConflicts)
                    {
                        ConsoleShareEndpointsLog.DependencyClosureConflict(logger, id, targetTier, conflicts.Count);
                        return TypedResults.Conflict(ApiResponse<ConsoleShareDependencyClosureResponse>.Failure(
                            "Dependency closure is not shareable by the target audience.",
                            BuildClosure(id, targetTier, conflicts)));
                    }

                    ConsoleShareEndpointsLog.DependencyClosureBypassed(logger, id, targetTier, principalId, conflicts.Count);
                }
            }

            await shareStore.UpdateAccessTierAsync(id, targetTier, principalId, context.RequestAborted).ConfigureAwait(false);

            // public-indexed implies the item is publicly readable; keep membership
            // visibility consistent. Other tiers leave visibility untouched — the
            // membership scope change is a separate content operation.
            if (targetTier == ConsoleShareAccessTier.PublicIndexed && item.Visibility != ConsoleVisibility.Public)
            {
                await contentStore.PatchAsync(id, new ConsoleContentItemPatch
                {
                    Visibility = ConsoleVisibility.Public,
                    UpdatedById = principalId,
                }, context.RequestAborted).ConfigureAwait(false);
            }

            ConsoleShareEndpointsLog.ShareAccessTierChanged(logger, id, oldTier, targetTier, principalId);

            var projection = await BuildProjectionAsync(id, contentStore, shareStore, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            if (projection is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            return TypedResults.Ok(ApiResponse<ConsoleShareProjection>.CreateSuccess(projection));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.access", ex);
            return TypedResults.Problem(
                title: "Console share access update failed",
                detail: "An internal error occurred while updating the Console share access tier.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleShareDependencyClosureResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleGetDependencies(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleDependencyClosureValidator validator,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context,
        [FromQuery] string? targetTier = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetTier) || !ConsoleEnumParser.TryParse(targetTier, out ConsoleShareAccessTier tier))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    "targetTier is required and must be one of: private, organization, public-link, public-indexed."));
            }

            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var conflicts = await validator.ValidateAsync(id, tier, MaxClosureDepth, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleShareDependencyClosureResponse>.CreateSuccess(BuildClosure(id, tier, conflicts)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.dependencies", ex);
            return TypedResults.Problem(
                title: "Console share dependency preview failed",
                detail: "An internal error occurred while evaluating the dependency closure.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsolePublicLinkListResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleListPublicLinks(
        string id,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var tokens = await shareStore.ListPublicLinksAsync(id, context.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsolePublicLinkListResponse>.CreateSuccess(new ConsolePublicLinkListResponse
            {
                ItemId = id,
                Tokens = tokens,
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.link.list", ex);
            return TypedResults.Problem(
                title: "Console public-link listing failed",
                detail: "An internal error occurred while listing public-link tokens.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsolePublicLinkToken>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<object>>, ProblemHttpResult>> HandleMintPublicLink(
        string id,
        MintPublicLinkRequest request,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            if (request.ExpiresAt is { } expiry && expiry <= now)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("expiresAt must be in the future."));
            }

            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var state = await shareStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            var effectiveTier = ConsoleShareMapper.ResolveEffectiveTier(state, item);
            if (effectiveTier is not (ConsoleShareAccessTier.PublicLink or ConsoleShareAccessTier.PublicIndexed))
            {
                return TypedResults.Conflict(ApiResponse<object>.Failure(
                    "Set the access tier to public-link or public-indexed before minting a public-link token."));
            }

            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var token = await shareStore.MintPublicLinkAsync(id, request.ExpiresAt, principalId, context.RequestAborted).ConfigureAwait(false);
            ConsoleShareEndpointsLog.PublicLinkMinted(logger, id, token.TokenId, principalId, token.ExpiresAt);
            return TypedResults.Ok(ApiResponse<ConsolePublicLinkToken>.CreateSuccess(token));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.link.mint", ex);
            return TypedResults.Problem(
                title: "Console public-link mint failed",
                detail: "An internal error occurred while minting the public-link token.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleExpirePublicLink(
        string id,
        string tokenId,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var revoked = await shareStore.ExpirePublicLinkAsync(id, tokenId, principalId, context.RequestAborted).ConfigureAwait(false);
            if (!revoked)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Public-link token not found."));

            ConsoleShareEndpointsLog.PublicLinkExpired(logger, id, tokenId, principalId);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Public-link token revoked."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.link.expire", ex);
            return TypedResults.Problem(
                title: "Console public-link revocation failed",
                detail: "An internal error occurred while revoking the public-link token.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleShareProjection>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<ConsoleShareDependencyClosureResponse>>, ProblemHttpResult>> HandleSetEmbed(
        string id,
        SetEmbedRequest request,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] IConsoleDependencyClosureValidator validator,
        [FromServices] IConsoleActionEvaluator evaluator,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            if (request.Audience.HasValue && !ConsoleEnumParser.IsDefined(request.Audience.Value))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"audience '{(int)request.Audience.Value}' is not a defined Console embed audience."));
            }
            if (request.Enabled && request.Audience is null)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("audience is required when enabling embed."));
            }

            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var principalId = ConsolePrincipal.ResolveActorId(context.User);

            // Enabling embed exposes the item to an external surface; require the
            // provenance closure to be shareable at public-link strength.
            if (request.Enabled)
            {
                var conflicts = await validator.ValidateAsync(id, ConsoleShareAccessTier.PublicLink, MaxClosureDepth, context.RequestAborted).ConfigureAwait(false);
                if (conflicts.Count > 0)
                {
                    if (!request.AllowDependencyConflicts)
                    {
                        ConsoleShareEndpointsLog.DependencyClosureConflict(logger, id, ConsoleShareAccessTier.PublicLink, conflicts.Count);
                        return TypedResults.Conflict(ApiResponse<ConsoleShareDependencyClosureResponse>.Failure(
                            "Dependency closure is not shareable for embedding.",
                            BuildClosure(id, ConsoleShareAccessTier.PublicLink, conflicts)));
                    }

                    ConsoleShareEndpointsLog.DependencyClosureBypassed(logger, id, ConsoleShareAccessTier.PublicLink, principalId, conflicts.Count);
                }
            }

            await shareStore.SetEmbedAsync(id, request.Enabled, request.Audience, principalId, context.RequestAborted).ConfigureAwait(false);
            ConsoleShareEndpointsLog.EmbedConfigured(logger, id, request.Enabled, request.Audience, principalId);

            var projection = await BuildProjectionAsync(id, contentStore, shareStore, evaluator, context.User, context.RequestAborted).ConfigureAwait(false);
            if (projection is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            return TypedResults.Ok(ApiResponse<ConsoleShareProjection>.CreateSuccess(projection));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.embed.set", ex);
            return TypedResults.Problem(
                title: "Console embed configuration failed",
                detail: "An internal error occurred while configuring embedding.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleEmbedToken>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<object>>, ProblemHttpResult>> HandleMintEmbedToken(
        string id,
        MintEmbedTokenRequest request,
        [FromServices] IConsoleContentStore contentStore,
        [FromServices] IConsoleShareStore shareStore,
        [FromServices] ILogger<ConsoleShareEndpointsLogCategory> logger,
        HttpContext context)
    {
        try
        {
            if (!TryValidate(request, out var validationError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }
            if (!ConsoleEnumParser.IsDefined(request.Audience))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"audience '{(int)request.Audience}' is not a defined Console embed audience."));
            }

            var item = await contentStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (item is null)
                return TypedResults.NotFound(ApiResponse<object>.Failure("Console content item not found."));

            var state = await shareStore.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (state is null || !state.EmbedEnabled)
            {
                return TypedResults.Conflict(ApiResponse<object>.Failure("Enable embedding before minting an embed token."));
            }
            if (state.EmbedAudience != request.Audience)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"audience must match the enabled embed audience ('{EmbedAudienceName(state.EmbedAudience)}')."));
            }

            var principalId = ConsolePrincipal.ResolveActorId(context.User);
            var ttl = TimeSpan.FromSeconds(request.TtlSeconds ?? DefaultEmbedTtlSeconds);
            var token = await shareStore.MintEmbedTokenAsync(id, request.Audience, ttl, principalId, context.RequestAborted).ConfigureAwait(false);
            ConsoleShareEndpointsLog.EmbedTokenMinted(logger, id, token.TokenId, request.Audience, principalId);
            return TypedResults.Ok(ApiResponse<ConsoleEmbedToken>.CreateSuccess(token));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "share.embed.mint", ex);
            return TypedResults.Problem(
                title: "Console embed token mint failed",
                detail: "An internal error occurred while minting the embed token.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<ConsoleShareProjection?> BuildProjectionAsync(
        string id,
        IConsoleContentStore contentStore,
        IConsoleShareStore shareStore,
        IConsoleActionEvaluator evaluator,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var item = await contentStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return null;

        var state = await shareStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var tokens = await shareStore.ListPublicLinksAsync(id, cancellationToken).ConfigureAwait(false);
        // Empty candidate set => evaluate the full Console verb set; reuses the
        // same RBAC evaluator the content endpoints use, so the Share panel's
        // share/embed/administer gating stays consistent.
        var actions = await evaluator.EvaluateItemActionsAsync(user, item, Array.Empty<ConsoleContentAction>(), cancellationToken).ConfigureAwait(false);
        return ConsoleShareMapper.BuildProjection(item, state, tokens, actions);
    }

    private static ConsoleShareDependencyClosureResponse BuildClosure(string itemId, ConsoleShareAccessTier tier, IReadOnlyList<ConsoleShareDependencyConflict> conflicts)
        => new()
        {
            ItemId = itemId,
            TargetTier = tier,
            IsCompatible = conflicts.Count == 0,
            Conflicts = conflicts,
        };

    private static bool RequiresClosureCheck(ConsoleShareAccessTier tier)
        => tier is ConsoleShareAccessTier.PublicLink or ConsoleShareAccessTier.PublicIndexed;

    private static string EmbedAudienceName(ConsoleEmbedAudience? audience) => audience switch
    {
        ConsoleEmbedAudience.Map => "map",
        ConsoleEmbedAudience.Content => "content",
        _ => "none",
    };

    private static bool TryValidate<TRequest>(TRequest request, out string error) where TRequest : notnull
    {
        var validationResults = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }
        error = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
        return false;
    }
}
