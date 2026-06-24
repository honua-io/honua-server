// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.Sharing;

/// <summary>
/// ArcGIS-compatible Portal community-group and item-sharing endpoints (#1868):
/// create/read/delete groups, add/remove group members, and share/unshare items to
/// users/groups/the org/the public.
/// </summary>
/// <remarks>
/// <para>
/// These mutate the in-memory <see cref="IPortalGroupStore"/> /
/// <see cref="IPortalItemSharingStore"/> overlays (no parallel durable store —
/// ADR-0049). Authorization composes the existing identity/role model rather than a
/// new one: every mutation requires an authenticated principal, and group/item
/// mutations require the caller to be the owner or hold the <c>admin</c> role
/// (resolved from the shared <see cref="ClaimsPrincipal"/>, exactly as the rest of
/// the auth subsystem). The whole surface is gated behind the same off-switch and
/// entitlement as the read surface, so it is never a silent surface.
/// </para>
/// </remarks>
internal static class SharingCommunityEndpoints
{
    private const string JsonContentType = "application/json";
    private const string AdminRole = "admin";

    /// <summary>Maps the community-group and item-sharing endpoints.</summary>
    public static IEndpointRouteBuilder MapSharingCommunityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/sharing/rest/community/createGroup", HandleCreateGroupAsync)
            .WithDisplayName("ArcGIS Portal Create Group")
            .WithName("SharingRestCommunityCreateGroup")
            .WithSummary("Create a community group")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GroupOperationResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/sharing/rest/community/groups/{groupId}", HandleGetGroupAsync)
            .WithDisplayName("ArcGIS Portal Group")
            .WithName("SharingRestCommunityGroup")
            .WithSummary("Fetch a community group and its membership")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GroupResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost("/sharing/rest/community/groups/{groupId}/delete", HandleDeleteGroupAsync)
            .WithDisplayName("ArcGIS Portal Delete Group")
            .WithName("SharingRestCommunityDeleteGroup")
            .WithSummary("Delete a community group")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GroupOperationResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost("/sharing/rest/community/groups/{groupId}/addUsers", HandleAddUsersAsync)
            .WithDisplayName("ArcGIS Portal Add Group Users")
            .WithName("SharingRestCommunityAddUsers")
            .WithSummary("Add users to a community group")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GroupMembershipResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost("/sharing/rest/community/groups/{groupId}/removeUsers", HandleRemoveUsersAsync)
            .WithDisplayName("ArcGIS Portal Remove Group Users")
            .WithName("SharingRestCommunityRemoveUsers")
            .WithSummary("Remove users from a community group")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GroupMembershipResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost("/sharing/rest/content/items/{itemId}/share", HandleShareItemAsync)
            .WithDisplayName("ArcGIS Portal Share Item")
            .WithName("SharingRestContentShareItem")
            .WithSummary("Share a portal item to users, groups, the org, or the public")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<ItemSharingResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost("/sharing/rest/content/items/{itemId}/unshare", HandleUnshareItemAsync)
            .WithDisplayName("ArcGIS Portal Unshare Item")
            .WithName("SharingRestContentUnshareItem")
            .WithSummary("Stop sharing a portal item with groups, the org, or the public")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<ItemSharingResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleCreateGroupAsync(
        HttpContext context,
        [FromServices] IPortalGroupStore groupStore,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateSharingSurface(context, logger);
        if (gate is not null)
        {
            return gate;
        }

        var owner = RequireAuthenticatedUser(context);
        if (owner is null)
        {
            return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to create a group.");
        }

        var form = await ReadFormOrQueryAsync(context).ConfigureAwait(false);
        var title = form.Get("title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "title is required.");
        }

        var record = await groupStore.CreateAsync(
            new PortalGroupRegistration(
                Title: title!,
                Description: form.Get("description"),
                Access: form.Get("access") ?? PortalGroupAccess.Private,
                Tags: SplitList(form.Get("tags"))),
            owner,
            context.RequestAborted).ConfigureAwait(false);

        var response = new GroupOperationResponse
        {
            Success = true,
            GroupId = record.Id,
            Group = ToGroup(record),
        };
        return Results.Json(response, SharingRestJsonContext.Default.GroupOperationResponse, contentType: JsonContentType);
    }

    private static async Task<IResult> HandleGetGroupAsync(
        HttpContext context,
        string groupId,
        [FromServices] IPortalGroupStore groupStore,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateSharingSurface(context, logger);
        if (gate is not null)
        {
            return gate;
        }

        var record = await groupStore.GetAsync(groupId, context.RequestAborted).ConfigureAwait(false);
        if (record is null || !CanSeeGroup(record, context.User))
        {
            // Don't distinguish "not found" from "not visible" so a private group's
            // existence is never leaked to a non-member.
            return StandardErrorHelpers.CreateNotFound(context, "Group does not exist or is inaccessible.");
        }

        return Results.Json(ToGroup(record), SharingRestJsonContext.Default.GroupResponse, contentType: JsonContentType);
    }

    private static async Task<IResult> HandleDeleteGroupAsync(
        HttpContext context,
        string groupId,
        [FromServices] IPortalGroupStore groupStore,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateSharingSurface(context, logger);
        if (gate is not null)
        {
            return gate;
        }

        var caller = RequireAuthenticatedUser(context);
        if (caller is null)
        {
            return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to delete a group.");
        }

        var record = await groupStore.GetAsync(groupId, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Group does not exist.");
        }

        if (!CanAdministerGroup(record, caller, context.User))
        {
            return StandardErrorHelpers.CreateForbidden(context, "Only the group owner or an administrator may delete the group.");
        }

        await groupStore.DeleteAsync(groupId, context.RequestAborted).ConfigureAwait(false);
        var response = new GroupOperationResponse { Success = true, GroupId = groupId };
        return Results.Json(response, SharingRestJsonContext.Default.GroupOperationResponse, contentType: JsonContentType);
    }

    private static Task<IResult> HandleAddUsersAsync(
        HttpContext context,
        string groupId,
        [FromServices] IPortalGroupStore groupStore,
        [FromServices] ILogger<SharingRestLog> logger)
        => MutateMembersAsync(context, groupId, groupStore, logger, add: true);

    private static Task<IResult> HandleRemoveUsersAsync(
        HttpContext context,
        string groupId,
        [FromServices] IPortalGroupStore groupStore,
        [FromServices] ILogger<SharingRestLog> logger)
        => MutateMembersAsync(context, groupId, groupStore, logger, add: false);

    private static async Task<IResult> MutateMembersAsync(
        HttpContext context,
        string groupId,
        IPortalGroupStore groupStore,
        ILogger<SharingRestLog> logger,
        bool add)
    {
        var gate = GateSharingSurface(context, logger);
        if (gate is not null)
        {
            return gate;
        }

        var caller = RequireAuthenticatedUser(context);
        if (caller is null)
        {
            return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to change group membership.");
        }

        var record = await groupStore.GetAsync(groupId, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Group does not exist.");
        }

        if (!CanAdministerGroup(record, caller, context.User))
        {
            return StandardErrorHelpers.CreateForbidden(context, "Only the group owner or an administrator may change membership.");
        }

        var form = await ReadFormOrQueryAsync(context).ConfigureAwait(false);
        var users = SplitList(form.Get("users"));

        var updated = add
            ? await groupStore.AddUsersAsync(groupId, users, context.RequestAborted).ConfigureAwait(false)
            : await groupStore.RemoveUsersAsync(groupId, users, context.RequestAborted).ConfigureAwait(false);

        var response = new GroupMembershipResponse
        {
            Success = true,
            GroupId = groupId,
            Members = updated?.Members ?? [],
        };
        return Results.Json(response, SharingRestJsonContext.Default.GroupMembershipResponse, contentType: JsonContentType);
    }

    private static Task<IResult> HandleShareItemAsync(
        HttpContext context,
        string itemId,
        [FromServices] IPortalItemSharingStore sharingStore,
        [FromServices] ILogger<SharingRestLog> logger)
        => ApplySharingAsync(context, itemId, sharingStore, logger, share: true);

    private static Task<IResult> HandleUnshareItemAsync(
        HttpContext context,
        string itemId,
        [FromServices] IPortalItemSharingStore sharingStore,
        [FromServices] ILogger<SharingRestLog> logger)
        => ApplySharingAsync(context, itemId, sharingStore, logger, share: false);

    private static async Task<IResult> ApplySharingAsync(
        HttpContext context,
        string itemId,
        IPortalItemSharingStore sharingStore,
        ILogger<SharingRestLog> logger,
        bool share)
    {
        var gate = GateSharingSurface(context, logger);
        if (gate is not null)
        {
            return gate;
        }

        var caller = RequireAuthenticatedUser(context);
        if (caller is null)
        {
            return StandardErrorHelpers.CreateUnauthorized(context, "Authentication is required to change item sharing.");
        }

        // Authorization: only the item's sharing owner (the first principal to share
        // it) or an administrator may change an item's sharing. This prevents any
        // authenticated user from exposing an arbitrary item to the public/org. The
        // first share of a never-shared item establishes ownership.
        var owner = await sharingStore.GetOwnerAsync(itemId, context.RequestAborted).ConfigureAwait(false);
        var isAdmin = IsAdmin(context.User);
        if (owner is not null &&
            !isAdmin &&
            !string.Equals(owner, caller, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateForbidden(
                context,
                "Only the item's owner or an administrator may change its sharing.");
        }

        var form = await ReadFormOrQueryAsync(context).ConfigureAwait(false);
        var everyone = ParseBool(form.Get("everyone"));
        var org = ParseBool(form.Get("org"));
        var groups = SplitList(form.Get("groups"));

        if (!everyone && !org && groups.Count == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "At least one of everyone, org, or groups must be specified.");
        }

        var request = new PortalItemShareRequest(everyone, org, groups);
        var state = share
            ? await sharingStore.ShareAsync(itemId, caller, request, context.RequestAborted).ConfigureAwait(false)
            : await sharingStore.UnshareAsync(itemId, request, context.RequestAborted).ConfigureAwait(false);

        var response = new ItemSharingResponse
        {
            ItemId = itemId,
            // ArcGIS share responses report notSharedWith; an empty array means the
            // requested change applied to every requested audience.
            NotSharedWith = [],
            Sharing = new ItemSharingState
            {
                Everyone = state.Everyone,
                Org = state.Org,
                Groups = state.GroupIds,
            },
        };
        return Results.Json(response, SharingRestJsonContext.Default.ItemSharingResponse, contentType: JsonContentType);
    }

    /// <summary>
    /// Gates the whole community/sharing surface on the same off-switch + entitlement
    /// as the read surface so it is never a silent surface. Returns an error result to
    /// short-circuit, or <see langword="null"/> when the request may proceed.
    /// </summary>
    private static IResult? GateSharingSurface(HttpContext context, ILogger<SharingRestLog> logger)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var enabled = configuration.GetValue(SharingRestEndpoints.ReadSurfaceEnabledKey, true);
        if (!enabled || !LicenseGate.IsEntitlementActive(context.RequestServices, SharingRestEndpoints.ReadEntitlementKey))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Portal sharing surface is not available.");
        }

        return null;
    }

    private static bool CanSeeGroup(PortalGroupRecord record, ClaimsPrincipal principal)
    {
        if (string.Equals(record.Access, PortalGroupAccess.Public, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (string.Equals(record.Access, PortalGroupAccess.Org, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var user = ResolveUsername(principal);
        return record.Members.Contains(user, StringComparer.OrdinalIgnoreCase) || IsAdmin(principal);
    }

    private static bool CanAdministerGroup(PortalGroupRecord record, string caller, ClaimsPrincipal principal)
        => string.Equals(record.Owner, caller, StringComparison.OrdinalIgnoreCase) || IsAdmin(principal);

    private static bool IsAdmin(ClaimsPrincipal principal)
        => principal.IsInRole(AdminRole) ||
            principal.Claims.Any(c =>
                c.Type == ClaimTypes.Role && string.Equals(c.Value, AdminRole, StringComparison.OrdinalIgnoreCase));

    private static string? RequireAuthenticatedUser(HttpContext context)
        => context.User.Identity?.IsAuthenticated == true ? ResolveUsername(context.User) : null;

    private static string ResolveUsername(ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name
            ?? "user";

    private static GroupResponse ToGroup(PortalGroupRecord record) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Description = record.Description,
        Owner = record.Owner,
        Access = record.Access,
        Tags = record.Tags,
        Members = record.Members,
        Created = record.Created.ToUnixTimeMilliseconds(),
        Modified = record.Modified.ToUnixTimeMilliseconds(),
    };

    private static List<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ParseBool(string? raw)
        => string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "1", StringComparison.Ordinal);

    private static async Task<FormOrQuery> ReadFormOrQueryAsync(HttpContext context)
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            return new FormOrQuery(form, null);
        }

        return new FormOrQuery(null, context.Request.Query);
    }

    private readonly struct FormOrQuery(IFormCollection? form, IQueryCollection? query)
    {
        private readonly IFormCollection? _form = form;
        private readonly IQueryCollection? _query = query;

        public string? Get(string key)
        {
            StringValues values = _form is not null ? _form[key] : _query![key];
            var value = values.FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
