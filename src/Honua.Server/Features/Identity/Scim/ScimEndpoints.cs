// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Identity.Scim.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Identity.Scim;

/// <summary>
/// SCIM 2.0 provisioning endpoints (#510). Exposes <c>/scim/v2/Users</c> and
/// <c>/scim/v2/Groups</c> CRUD so an external identity provider can provision and
/// deprovision users and map groups to Honua RBAC roles. Authenticated by a static bearer
/// token (<see cref="ScimProvisioningOptions.BearerToken"/>) presented by the IdP.
/// </summary>
internal static partial class ScimEndpoints
{
    private const string BearerPrefix = "Bearer ";
    private const int MaxPageSize = 200;

    /// <summary>Log category for SCIM endpoints.</summary>
    internal sealed class ScimEndpointsLog;

    /// <summary>
    /// Maps the SCIM 2.0 provisioning endpoints. Authentication is enforced per-request
    /// against the configured bearer token rather than the admin authorization policy, so an
    /// IdP service-account token does not need an interactive admin session.
    /// </summary>
    public static void MapScimEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var users = endpoints.MapGroup("/scim/v2/Users")
            .WithTags("Identity", "SCIM")
            .AllowAnonymous();

        users.MapGet("/", HandleListUsers).WithDisplayName("SCIM List Users");
        users.MapPost("/", HandleCreateUser).WithDisplayName("SCIM Create User");
        users.MapGet("/{id}", HandleGetUser).WithDisplayName("SCIM Get User");
        users.MapPut("/{id}", HandleReplaceUser).WithDisplayName("SCIM Replace User");
        users.MapPatch("/{id}", HandlePatchUser).WithDisplayName("SCIM Patch User");
        users.MapDelete("/{id}", HandleDeleteUser).WithDisplayName("SCIM Delete User");

        var groups = endpoints.MapGroup("/scim/v2/Groups")
            .WithTags("Identity", "SCIM")
            .AllowAnonymous();

        groups.MapGet("/", HandleListGroups).WithDisplayName("SCIM List Groups");
        groups.MapPost("/", HandleCreateGroup).WithDisplayName("SCIM Create Group");
        groups.MapGet("/{id}", HandleGetGroup).WithDisplayName("SCIM Get Group");
        groups.MapPut("/{id}", HandleReplaceGroup).WithDisplayName("SCIM Replace Group");
        groups.MapPatch("/{id}", HandlePatchGroup).WithDisplayName("SCIM Patch Group");
        groups.MapDelete("/{id}", HandleDeleteGroup).WithDisplayName("SCIM Delete Group");
    }

    // ---- Users ------------------------------------------------------------------------

    private static async Task<IResult> HandleListUsers(
        HttpContext context,
        [FromServices] IScimUserStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var (startIndex, count) = ReadPaging(context);
        var query = new ScimUserQuery
        {
            UserNameEquals = ParseEqFilter(context.Request.Query["filter"], "userName"),
            StartIndex = startIndex,
            Count = count,
        };

        var page = await store.ListUsersAsync(query, context.RequestAborted).ConfigureAwait(false);
        var response = new ScimListResponse<ScimUser>
        {
            TotalResults = page.TotalCount,
            StartIndex = startIndex,
            ItemsPerPage = page.Users.Count,
            Resources = page.Users.Select(ToScimUser).ToList(),
        };

        ScimLog.UsersListed(logger, page.Users.Count, page.TotalCount);
        return ScimJson(response, ScimJsonContext.Default.ScimListResponseScimUser, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleCreateUser(
        HttpContext context,
        [FromServices] IScimUserStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var input = await ReadUserAsync(context).ConfigureAwait(false);
        if (input is null || string.IsNullOrWhiteSpace(input.UserName))
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "userName is required.", "invalidValue");
        }

        var created = await store.CreateUserAsync(
            new ScimUserProvisioning
            {
                UserName = input.UserName.Trim(),
                DisplayName = input.DisplayName,
                Email = SelectEmail(input.Emails),
                Active = input.Active,
            },
            context.RequestAborted).ConfigureAwait(false);

        if (created is null)
        {
            return ScimErrorResult(StatusCodes.Status409Conflict, "A user with that userName already exists.", "uniqueness");
        }

        ScimLog.UserProvisioned(logger, created.UserId);
        return ScimJson(ToScimUser(created), ScimJsonContext.Default.ScimUser, StatusCodes.Status201Created);
    }

    private static async Task<IResult> HandleGetUser(
        string id,
        HttpContext context,
        [FromServices] IScimUserStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var user = await store.GetUserAsync(id, context.RequestAborted).ConfigureAwait(false);
        return user is null
            ? ScimErrorResult(StatusCodes.Status404NotFound, "User not found.")
            : ScimJson(ToScimUser(user), ScimJsonContext.Default.ScimUser, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleReplaceUser(
        string id,
        HttpContext context,
        [FromServices] IScimUserStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var input = await ReadUserAsync(context).ConfigureAwait(false);
        if (input is null || string.IsNullOrWhiteSpace(input.UserName))
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "userName is required.", "invalidValue");
        }

        var existing = await store.GetUserAsync(id, context.RequestAborted).ConfigureAwait(false);
        var replaced = await store.ReplaceUserAsync(
            id,
            new ScimUserProvisioning
            {
                UserName = input.UserName.Trim(),
                DisplayName = input.DisplayName,
                Email = SelectEmail(input.Emails),
                Active = input.Active,
                // Roles are owned by group membership sync; PUT preserves the current set.
                Roles = existing?.Roles ?? [],
            },
            context.RequestAborted).ConfigureAwait(false);

        if (replaced is null)
        {
            return ScimErrorResult(StatusCodes.Status404NotFound, "User not found.");
        }

        ScimLog.UserReplaced(logger, replaced.UserId);
        return ScimJson(ToScimUser(replaced), ScimJsonContext.Default.ScimUser, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandlePatchUser(
        string id,
        HttpContext context,
        [FromServices] IScimUserStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var patch = await ReadPatchAsync(context).ConfigureAwait(false);
        if (patch?.Operations is null || patch.Operations.Count == 0)
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "At least one PATCH operation is required.", "invalidValue");
        }

        // The dominant IdP PATCH on a user is toggling "active" for deprovision/reactivate.
        if (!TryResolveActivePatch(patch, out var active))
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "Unsupported PATCH operation; only the 'active' attribute is patchable.", "invalidValue");
        }

        var updated = await store.SetActiveAsync(id, active, context.RequestAborted).ConfigureAwait(false);
        if (updated is null)
        {
            return ScimErrorResult(StatusCodes.Status404NotFound, "User not found.");
        }

        ScimLog.UserActiveChanged(logger, updated.UserId, active);
        return ScimJson(ToScimUser(updated), ScimJsonContext.Default.ScimUser, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleDeleteUser(
        string id,
        HttpContext context,
        [FromServices] IScimUserStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var deleted = await store.DeleteUserAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (!deleted)
        {
            return ScimErrorResult(StatusCodes.Status404NotFound, "User not found.");
        }

        ScimLog.UserDeprovisioned(logger, id);
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    // ---- Groups -----------------------------------------------------------------------

    private static async Task<IResult> HandleListGroups(
        HttpContext context,
        [FromServices] IScimGroupStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var (startIndex, count) = ReadPaging(context);
        var query = new ScimGroupQuery
        {
            DisplayNameEquals = ParseEqFilter(context.Request.Query["filter"], "displayName"),
            StartIndex = startIndex,
            Count = count,
        };

        var page = await store.ListGroupsAsync(query, context.RequestAborted).ConfigureAwait(false);
        var response = new ScimListResponse<ScimGroupResource>
        {
            TotalResults = page.TotalCount,
            StartIndex = startIndex,
            ItemsPerPage = page.Groups.Count,
            Resources = page.Groups.Select(ToScimGroup).ToList(),
        };

        return ScimJson(response, ScimJsonContext.Default.ScimListResponseScimGroupResource, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleCreateGroup(
        HttpContext context,
        [FromServices] IScimGroupStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var input = await ReadGroupAsync(context).ConfigureAwait(false);
        if (input is null || string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "displayName is required.", "invalidValue");
        }

        var created = await store.CreateGroupAsync(
            new ScimGroupProvisioning
            {
                DisplayName = input.DisplayName.Trim(),
                MemberUserIds = ExtractMemberIds(input.Members),
            },
            context.RequestAborted).ConfigureAwait(false);

        if (created is null)
        {
            return ScimErrorResult(StatusCodes.Status409Conflict, "A group with that displayName already exists.", "uniqueness");
        }

        ScimLog.GroupProvisioned(logger, created.DisplayName);
        return ScimJson(ToScimGroup(created), ScimJsonContext.Default.ScimGroupResource, StatusCodes.Status201Created);
    }

    private static async Task<IResult> HandleGetGroup(
        string id,
        HttpContext context,
        [FromServices] IScimGroupStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var group = await store.GetGroupAsync(id, context.RequestAborted).ConfigureAwait(false);
        return group is null
            ? ScimErrorResult(StatusCodes.Status404NotFound, "Group not found.")
            : ScimJson(ToScimGroup(group), ScimJsonContext.Default.ScimGroupResource, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleReplaceGroup(
        string id,
        HttpContext context,
        [FromServices] IScimGroupStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var input = await ReadGroupAsync(context).ConfigureAwait(false);
        if (input is null || string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "displayName is required.", "invalidValue");
        }

        var replaced = await store.ReplaceGroupAsync(
            id,
            new ScimGroupProvisioning
            {
                DisplayName = input.DisplayName.Trim(),
                MemberUserIds = ExtractMemberIds(input.Members),
            },
            context.RequestAborted).ConfigureAwait(false);

        if (replaced is null)
        {
            return ScimErrorResult(StatusCodes.Status404NotFound, "Group not found.");
        }

        ScimLog.GroupReplaced(logger, replaced.DisplayName);
        return ScimJson(ToScimGroup(replaced), ScimJsonContext.Default.ScimGroupResource, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandlePatchGroup(
        string id,
        HttpContext context,
        [FromServices] IScimGroupStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var patch = await ReadPatchAsync(context).ConfigureAwait(false);
        if (patch?.Operations is null || patch.Operations.Count == 0)
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "At least one PATCH operation is required.", "invalidValue");
        }

        if (!TryResolveMemberPatch(patch, out var change))
        {
            return ScimErrorResult(StatusCodes.Status400BadRequest, "Unsupported PATCH operation; only the 'members' attribute is patchable.", "invalidValue");
        }

        var updated = await store.UpdateMembersAsync(id, change, context.RequestAborted).ConfigureAwait(false);
        if (updated is null)
        {
            return ScimErrorResult(StatusCodes.Status404NotFound, "Group not found.");
        }

        ScimLog.GroupMembersChanged(logger, updated.DisplayName, change.Add.Count, change.Remove.Count);
        return ScimJson(ToScimGroup(updated), ScimJsonContext.Default.ScimGroupResource, StatusCodes.Status200OK);
    }

    private static async Task<IResult> HandleDeleteGroup(
        string id,
        HttpContext context,
        [FromServices] IScimGroupStore store,
        [FromServices] IOptions<ScimProvisioningOptions> options,
        [FromServices] ILogger<ScimEndpointsLog> logger)
    {
        if (!Authenticate(context, options.Value))
        {
            return Unauthorized();
        }

        var deleted = await store.DeleteGroupAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (!deleted)
        {
            return ScimErrorResult(StatusCodes.Status404NotFound, "Group not found.");
        }

        ScimLog.GroupDeleted(logger, id);
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    // ---- Authentication ---------------------------------------------------------------

    /// <summary>
    /// Validates the SCIM bearer token in constant time. Returns <see langword="false"/> when
    /// provisioning is disabled (no token configured) or the presented token does not match.
    /// </summary>
    private static bool Authenticate(HttpContext context, ScimProvisioningOptions options)
    {
        var configured = options.BearerToken;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // No token configured => SCIM provisioning is disabled; reject everything.
            return false;
        }

        if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var header))
        {
            return false;
        }

        var raw = header.ToString();
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = raw[BearerPrefix.Length..].Trim();
        return FixedTimeEquals(presented, configured);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    // ---- Mapping ----------------------------------------------------------------------

    private static ScimUser ToScimUser(ManagedUser user) => new()
    {
        Id = user.UserId,
        UserName = user.UserId,
        DisplayName = user.DisplayName,
        Active = user.IsActive,
        Emails = string.IsNullOrWhiteSpace(user.Email)
            ? null
            : [new ScimEmail { Value = user.Email, Primary = true, Type = "work" }],
        Meta = new ScimMeta
        {
            ResourceType = "User",
            Created = user.CreatedAt,
            LastModified = user.UpdatedAt,
            Location = $"/scim/v2/Users/{user.UserId}",
        },
    };

    private static ScimGroupResource ToScimGroup(ScimGroup group) => new()
    {
        Id = group.GroupId,
        DisplayName = group.DisplayName,
        Members = group.MemberUserIds.Select(m => new ScimMember { Value = m }).ToList(),
        Meta = new ScimMeta
        {
            ResourceType = "Group",
            Created = group.CreatedAt,
            LastModified = group.UpdatedAt,
            Location = $"/scim/v2/Groups/{group.GroupId}",
        },
    };

    private static string? SelectEmail(IReadOnlyList<ScimEmail>? emails)
    {
        if (emails is null || emails.Count == 0)
        {
            return null;
        }

        var primary = emails.FirstOrDefault(e => e.Primary == true && !string.IsNullOrWhiteSpace(e.Value));
        return (primary ?? emails.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Value)))?.Value;
    }

    private static List<string> ExtractMemberIds(IReadOnlyList<ScimMember>? members)
        => members is null
            ? []
            : members
                .Select(m => m.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToList();

    // ---- Patch resolution -------------------------------------------------------------

    private static bool TryResolveActivePatch(ScimPatchRequest patch, out bool active)
    {
        active = true;
        var resolved = false;

        foreach (var op in patch.Operations!)
        {
            var verb = op.Op?.Trim().ToLowerInvariant();
            if (verb is not ("add" or "replace"))
            {
                return false;
            }

            // Accept both path-form ({"path":"active","value":true}) and value-object form
            // ({"value":{"active":false}}) which different IdPs emit.
            if (string.Equals(op.Path, "active", StringComparison.OrdinalIgnoreCase) && op.Value.HasValue)
            {
                if (!TryReadBool(op.Value.Value, out active))
                {
                    return false;
                }

                resolved = true;
            }
            else if (string.IsNullOrEmpty(op.Path) && op.Value is { ValueKind: JsonValueKind.Object } obj
                     && obj.TryGetProperty("active", out var activeProp))
            {
                if (!TryReadBool(activeProp, out active))
                {
                    return false;
                }

                resolved = true;
            }
            else
            {
                return false;
            }
        }

        return resolved;
    }

    private static bool TryReadBool(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed):
                value = parsed;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryResolveMemberPatch(ScimPatchRequest patch, out ScimGroupMemberChange change)
    {
        var add = new List<string>();
        var remove = new List<string>();
        var resolved = false;

        foreach (var op in patch.Operations!)
        {
            var verb = op.Op?.Trim().ToLowerInvariant();
            if (!string.Equals(op.Path, "members", StringComparison.OrdinalIgnoreCase) || op.Value is null)
            {
                return Fail(out change);
            }

            var ids = ReadMemberValues(op.Value.Value);
            switch (verb)
            {
                case "add":
                    add.AddRange(ids);
                    resolved = true;
                    break;
                case "remove":
                    remove.AddRange(ids);
                    resolved = true;
                    break;
                default:
                    return Fail(out change);
            }
        }

        change = new ScimGroupMemberChange { Add = add, Remove = remove };
        return resolved;

        static bool Fail(out ScimGroupMemberChange c)
        {
            c = new ScimGroupMemberChange();
            return false;
        }
    }

    private static List<string> ReadMemberValues(JsonElement value)
    {
        var ids = new List<string>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("value", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                var id = v.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var id = element.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }
        }

        return ids;
    }

    // ---- Request parsing & responses --------------------------------------------------

    private static (int StartIndex, int Count) ReadPaging(HttpContext context)
    {
        var startIndex = 1;
        var count = 100;

        if (int.TryParse(context.Request.Query["startIndex"], out var si) && si >= 1)
        {
            startIndex = si;
        }

        if (int.TryParse(context.Request.Query["count"], out var c) && c >= 0)
        {
            count = Math.Min(c, MaxPageSize);
        }

        return (startIndex, count);
    }

    /// <summary>
    /// Parses a SCIM <c>filter</c> of the form <c>attribute eq "value"</c> for a single
    /// supported attribute. Returns null when the filter is absent or not an equality match on
    /// the requested attribute (broader filter grammar is intentionally unsupported).
    /// </summary>
    private static string? ParseEqFilter(string? filter, string attribute)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var trimmed = filter.Trim();
        var prefix = attribute + " eq ";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = trimmed[prefix.Length..].Trim();
        if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
        {
            return rest[1..^1];
        }

        return rest;
    }

    private static async Task<ScimUser?> ReadUserAsync(HttpContext context)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body, ScimJsonContext.Default.ScimUser, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ScimGroupResource?> ReadGroupAsync(HttpContext context)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body, ScimJsonContext.Default.ScimGroupResource, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ScimPatchRequest?> ReadPatchAsync(HttpContext context)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(
                context.Request.Body, ScimJsonContext.Default.ScimPatchRequest, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult ScimJson<T>(T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, int statusCode)
        => Results.Json(body, typeInfo, contentType: ScimSchemas.ContentType, statusCode: statusCode);

    private static IResult Unauthorized()
        => ScimErrorResult(StatusCodes.Status401Unauthorized, "A valid SCIM bearer token is required.");

    private static IResult ScimErrorResult(int statusCode, string detail, string? scimType = null)
        => Results.Json(
            new ScimError
            {
                Status = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Detail = detail,
                ScimType = scimType,
            },
            ScimJsonContext.Default.ScimError,
            contentType: ScimSchemas.ContentType,
            statusCode: statusCode);
}
