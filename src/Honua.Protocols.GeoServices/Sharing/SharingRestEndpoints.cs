// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Portal.Abstractions;
using Honua.Core.Features.Portal.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.Sharing;

/// <summary>
/// ArcGIS-compatible <c>/sharing/rest</c> endpoints. Currently exposes
/// <c>generateToken</c>; additional sharing surface is added in follow-up
/// tickets.
/// </summary>
public static class SharingRestEndpoints
{
    internal const string EntitlementKey = "identity.portal-token";
    internal const string EntitlementFeatureName = "ArcGIS Portal Token Issuance";

    /// <summary>Entitlement key gating the read-only Portal/Sharing discovery surface.</summary>
    internal const string ReadEntitlementKey = "identity.portal-sharing";
    internal const string ReadEntitlementFeatureName = "ArcGIS Portal Sharing Read Surface";

    private const string JsonContentType = "application/json";

    // The Portal facade only accepts the ArcGIS family of services as items, so
    // the search result page size is bounded to a sane default/maximum.
    private const int DefaultSearchPageSize = 10;
    private const int MaxSearchPageSize = 100;

    /// <summary>
    /// Maps the <c>/sharing/rest/generateToken</c> POST and GET endpoints.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder to extend.</param>
    /// <returns>The original builder, to support fluent chaining.</returns>
    public static IEndpointRouteBuilder MapSharingRestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/sharing/rest/generateToken", HandleGenerateTokenAsync)
            .WithDisplayName("ArcGIS Portal Generate Token")
            .WithName("SharingRestGenerateTokenPost")
            .WithSummary("Issue an ArcGIS-compatible portal token")
            .WithDescription("Issues an opaque bearer token bound to either the supplied referer or client IP for the configured TTL.")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GenerateTokenResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status402PaymentRequired);

        endpoints.MapGet("/sharing/rest/generateToken", HandleGenerateTokenAsync)
            .WithDisplayName("ArcGIS Portal Generate Token (GET)")
            .WithName("SharingRestGenerateTokenGet")
            .WithSummary("Issue an ArcGIS-compatible portal token via query parameters")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<GenerateTokenResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status402PaymentRequired);

        endpoints.MapSharingRestReadEndpoints();
        endpoints.MapSharingOAuth2Endpoints();

        return endpoints;
    }

    /// <summary>
    /// Maps the read-only Portal/Sharing REST discovery surface (#1243):
    /// <c>info</c>, <c>portals/self</c>, <c>community/self</c>, <c>search</c>, and
    /// <c>content/items/{id}</c> (+ <c>/data</c>). All routes are off-by-default
    /// and entitlement-gated; the whole surface returns 404 when disabled so it is
    /// never a silent discovery surface.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder to extend.</param>
    /// <returns>The original builder, to support fluent chaining.</returns>
    private static IEndpointRouteBuilder MapSharingRestReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sharing/rest/info", HandleInfoAsync)
            .WithDisplayName("ArcGIS Portal Sharing Info")
            .WithName("SharingRestInfo")
            .WithSummary("Portal version and authentication info")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<SharingInfoResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        // The /rest/info alias is already served by the GeoServices catalog
        // endpoint (GeoservicesCatalogEndpoints.HandleGetRestInfo) with an
        // equivalent authInfo shape; we do not re-map it here to avoid an
        // ambiguous route. The Portal-specific authInfo lives at
        // /sharing/rest/info.
        endpoints.MapGet("/sharing/rest/portals/self", HandlePortalSelfAsync)
            .WithDisplayName("ArcGIS Portal Self")
            .WithName("SharingRestPortalsSelf")
            .WithSummary("RBAC-scoped portal/user self description")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<PortalSelfResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/sharing/rest/community/self", HandleCommunitySelfAsync)
            .WithDisplayName("ArcGIS Portal Community Self")
            .WithName("SharingRestCommunitySelf")
            .WithSummary("RBAC-scoped user self description")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<CommunitySelfResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/sharing/rest/search", HandleSearchAsync)
            .WithDisplayName("ArcGIS Portal Search")
            .WithName("SharingRestSearch")
            .WithSummary("Search visible portal items with paging")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<SearchResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/sharing/rest/content/items/{id}", HandleContentItemAsync)
            .WithDisplayName("ArcGIS Portal Content Item")
            .WithName("SharingRestContentItem")
            .WithSummary("Fetch a single portal item by id")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<PortalItem>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/sharing/rest/content/items/{id}/data", HandleContentItemDataAsync)
            .WithDisplayName("ArcGIS Portal Content Item Data")
            .WithName("SharingRestContentItemData")
            .WithSummary("Fetch a single portal item's data document by id")
            .WithTags("GeoServices Sharing")
            .AllowAnonymous()
            .Produces<PortalItem>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> HandleGenerateTokenAsync(
        HttpContext context,
        [FromServices] IPortalTokenIssuer tokenIssuer,
        [FromServices] IPortalCredentialVerifier credentialVerifier,
        [FromServices] IOptions<PortalTokenAuthenticationOptions> options,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Portal token issuance is disabled.");
        }

        var entitlementFailure = LicenseGate.RequireEntitlement(
            context,
            EntitlementKey,
            EntitlementFeatureName,
            logger);
        if (entitlementFailure is not null)
        {
            return entitlementFailure;
        }

        var (username, password, clientType, refererInput, expirationMinutes, format, formatValid) =
            await ReadParametersAsync(context).ConfigureAwait(false);

        if (!formatValid)
        {
            PortalTokenLog.TokenIssuanceRejected(logger, "unsupported response format");
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be 'json'.");
        }

        if (settings.RequireHttps && !IsHttpsRequest(context))
        {
            PortalTokenLog.TokenIssuanceRejected(logger, "https required");
            return StandardErrorHelpers.CreateForbidden(
                context,
                "Token issuance requires a secure (HTTPS) transport.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            PortalTokenLog.TokenIssuanceRejected(logger, "missing credentials");
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Username and password are required.");
        }

        var binding = ResolveBinding(context, clientType, refererInput);
        if (binding is null)
        {
            PortalTokenLog.TokenIssuanceRejected(logger, "missing binding");
            return StandardErrorHelpers.CreateBadRequest(
                context,
                clientType == PortalTokenClientType.Referer
                    ? "A 'referer' header or parameter is required when 'client' is 'referer'."
                    : "Client IP could not be determined for an 'ip' binding.");
        }

        var verified = await credentialVerifier
            .VerifyAsync(username!, password!, context.RequestAborted)
            .ConfigureAwait(false);
        if (verified is null)
        {
            PortalTokenLog.TokenIssuanceRejected(logger, "invalid credentials");
            return StandardErrorHelpers.CreateUnauthorized(
                context,
                "Username or password is incorrect.");
        }

        var ttlMinutes = ResolveExpirationMinutes(expirationMinutes, settings);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes);

        var issuance = await tokenIssuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: verified.PrincipalId,
                DisplayName: verified.DisplayName,
                TenantId: verified.TenantId ?? tenantContext.TenantId,
                Roles: verified.Roles,
                ClientType: clientType,
                BindingValue: binding,
                ExpiresAt: expiresAt),
            context.RequestAborted).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
#pragma warning disable CA1873 // LogValueRedactor.Hash / ToString only invoked inside the IsEnabled gate above
            PortalTokenLog.TokenIssued(
                logger,
                LogValueRedactor.Hash(verified.PrincipalId),
                LogValueRedactor.Hash(verified.TenantId ?? tenantContext.TenantId ?? string.Empty),
                clientType.ToString(),
                issuance.ExpiresAt);
#pragma warning restore CA1873
        }

        var response = new GenerateTokenResponse
        {
            Token = issuance.Token,
            Expires = issuance.ExpiresAt.ToUnixTimeMilliseconds(),
            Ssl = true,
        };

        return Results.Json(response, SharingRestJsonContext.Default.GenerateTokenResponse, contentType: JsonContentType);
    }

    private static IResult HandleInfoAsync(
        HttpContext context,
        string? f,
        [FromServices] IOptions<PortalTokenAuthenticationOptions> tokenOptions,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateReadSurface(context, f, logger);
        if (gate is not null)
        {
            return gate;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var response = new SharingInfoResponse
        {
            AuthInfo = new SharingAuthInfo
            {
                IsTokenBasedSecurity = tokenOptions.Value.Enabled,
                TokenServicesUrl = $"{baseUrl.TrimEnd('/')}/sharing/rest/generateToken",
            },
        };

        return Results.Json(response, SharingRestJsonContext.Default.SharingInfoResponse, contentType: JsonContentType);
    }

    private static IResult HandlePortalSelfAsync(
        HttpContext context,
        string? f,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateReadSurface(context, f, logger);
        if (gate is not null)
        {
            return gate;
        }

        var principal = context.User;
        var user = principal.Identity?.IsAuthenticated == true
            ? new PortalUser
            {
                Username = ResolveUsername(principal),
                FullName = ResolveDisplayName(principal),
            }
            : null;

        var response = new PortalSelfResponse
        {
            Id = "0123456789ABCDEF",
            Name = "Honua",
            User = user,
        };

        return Results.Json(response, SharingRestJsonContext.Default.PortalSelfResponse, contentType: JsonContentType);
    }

    private static IResult HandleCommunitySelfAsync(
        HttpContext context,
        string? f,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateReadSurface(context, f, logger);
        if (gate is not null)
        {
            return gate;
        }

        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            // community/self describes the *authenticated* user; anonymous callers
            // get the Esri-shaped 401 rather than an anonymous user document.
            return StandardErrorHelpers.CreateUnauthorized(
                context,
                "Authentication is required to describe the current user.");
        }

        var response = new CommunitySelfResponse
        {
            Username = ResolveUsername(principal),
            FullName = ResolveDisplayName(principal),
        };

        return Results.Json(response, SharingRestJsonContext.Default.CommunitySelfResponse, contentType: JsonContentType);
    }

    private static async Task<IResult> HandleSearchAsync(
        HttpContext context,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IPortalItemProjector projector,
        [FromServices] ILogger<SharingRestLog> logger)
    {
        var gate = GateReadSurface(context, f, logger);
        if (gate is not null)
        {
            return gate;
        }

        var query = ReadFirst(context.Request.Query["q"]) ?? string.Empty;
        var (start, num) = ResolvePaging(context.Request.Query);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var visible = projector.ProjectVisibleItems(snapshot, context.User, baseUrl);

        var filtered = ApplyQuery(visible, query);
        var sorted = ApplySort(filtered, context.Request.Query);

        var total = sorted.Count;
        // Esri start is 1-based; clamp the slice to the available range.
        var skip = Math.Max(0, start - 1);
        var page = skip >= total
            ? Array.Empty<PortalItem>()
            : sorted.Skip(skip).Take(num).ToArray();
        var nextStart = skip + page.Length < total ? skip + page.Length + 1 : -1;

        var response = new SearchResponse
        {
            Query = query,
            Total = total,
            Start = total == 0 ? 0 : start,
            Num = page.Length,
            NextStart = nextStart,
            Results = page,
        };

        return Results.Json(response, SharingRestJsonContext.Default.SearchResponse, contentType: JsonContentType);
    }

    private static Task<IResult> HandleContentItemAsync(
        HttpContext context,
        string id,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IPortalItemProjector projector,
        [FromServices] ILogger<SharingRestLog> logger)
        => ResolveItemAsync(context, id, f, graphProvider, projector, logger);

    private static Task<IResult> HandleContentItemDataAsync(
        HttpContext context,
        string id,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] IPortalItemProjector projector,
        [FromServices] ILogger<SharingRestLog> logger)
        => ResolveItemAsync(context, id, f, graphProvider, projector, logger);

    private static async Task<IResult> ResolveItemAsync(
        HttpContext context,
        string id,
        string? f,
        IMetadataV2GraphProvider graphProvider,
        IPortalItemProjector projector,
        ILogger<SharingRestLog> logger)
    {
        var gate = GateReadSurface(context, f, logger);
        if (gate is not null)
        {
            return gate;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var item = projector.ProjectItem(snapshot, context.User, id, baseUrl);
        if (item is null)
        {
            // The projector returns null both for unknown ids and for items the
            // principal cannot read, so the facade never leaks hidden items.
            return StandardErrorHelpers.CreateNotFound(context, "Item does not exist or is inaccessible.");
        }

        return Results.Json(item, SharingRestJsonContext.Default.PortalItem, contentType: JsonContentType);
    }

    /// <summary>
    /// Configuration key that explicitly disables the read-only Portal/Sharing
    /// discovery surface independent of licensing. Defaults to enabled; operators
    /// opt out by setting it to <c>false</c>.
    /// </summary>
    internal const string ReadSurfaceEnabledKey = "Sharing:ReadSurface:Enabled";

    /// <summary>
    /// Gates the read surface in priority order: (1) the operator enable flag,
    /// (2) the read-surface entitlement, then (3) the response format. When the
    /// surface is disabled or unlicensed the whole surface 404s — it is never a
    /// silent discovery surface and never reveals itself via a 400. Returns an
    /// error result to short-circuit the request, or <see langword="null"/> when
    /// the request may proceed.
    /// </summary>
    private static IResult? GateReadSurface(HttpContext context, string? format, ILogger<SharingRestLog> logger)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var enabled = configuration.GetValue(ReadSurfaceEnabledKey, true);
        if (!enabled || !LicenseGate.IsEntitlementActive(context.RequestServices, ReadEntitlementKey))
        {
            return StandardErrorHelpers.CreateNotFound(context, "Portal sharing read surface is not available.");
        }

        if (!IsSupportedFormat(format))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be 'json' or 'pjson'.");
        }

        return null;
    }

    private static (int Start, int Num) ResolvePaging(IQueryCollection query)
    {
        var start = 1;
        if (int.TryParse(ReadFirst(query["start"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStart) &&
            parsedStart > 0)
        {
            start = parsedStart;
        }

        var num = DefaultSearchPageSize;
        if (int.TryParse(ReadFirst(query["num"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNum) &&
            parsedNum > 0)
        {
            num = Math.Min(parsedNum, MaxSearchPageSize);
        }

        return (start, num);
    }

    /// <summary>
    /// Applies a coarse, case-insensitive Portal <c>q</c> filter over the projected
    /// items. Supports the common <c>type:</c>, <c>owner:</c>, and <c>tags:</c>
    /// field qualifiers plus free-text matching against title/snippet/tags; any
    /// other qualifier degrades to free-text so unsupported syntax never errors.
    /// </summary>
    private static List<PortalItem> ApplyQuery(IReadOnlyList<PortalItem> items, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return items.ToList();
        }

        var terms = TokenizeQuery(query);
        if (terms.Count == 0)
        {
            return items.ToList();
        }

        var result = new List<PortalItem>(items.Count);
        foreach (var item in items)
        {
            if (terms.All(term => MatchesTerm(item, term)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a Portal <c>q</c> expression into terms on whitespace while keeping
    /// double-quoted phrases (including the common <c>field:"two words"</c> form)
    /// intact, so multi-word qualifier values survive tokenizing.
    /// </summary>
    private static List<string> TokenizeQuery(string query)
    {
        var terms = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in query)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    terms.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return terms;
    }

    private static bool MatchesTerm(PortalItem item, string term)
    {
        var separator = term.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && separator < term.Length - 1)
        {
            var field = term[..separator];
            var value = term[(separator + 1)..].Trim('"');
            switch (field.ToLowerInvariant())
            {
                case "type":
                    return item.Type.Contains(value, StringComparison.OrdinalIgnoreCase);
                case "owner":
                    return string.Equals(item.Owner, value, StringComparison.OrdinalIgnoreCase);
                case "tags":
                    return item.Tags.Any(tag => string.Equals(tag, value, StringComparison.OrdinalIgnoreCase));
                case "id":
                    return string.Equals(item.Id, value, StringComparison.OrdinalIgnoreCase);
                case "title":
                    return item.Title.Contains(value, StringComparison.OrdinalIgnoreCase);
                default:
                    return FreeTextMatch(item, term);
            }
        }

        return FreeTextMatch(item, term);
    }

    private static bool FreeTextMatch(PortalItem item, string term)
    {
        var trimmed = term.Trim('"');
        return item.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            (item.Snippet?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.Description?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
            item.Tags.Any(tag => tag.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies the Portal <c>sortField</c>/<c>sortOrder</c> options over the
    /// projected items. The projector already returns items title-sorted; this
    /// honours explicit overrides for <c>title</c>, <c>created</c>, and
    /// <c>modified</c>.
    /// </summary>
    private static List<PortalItem> ApplySort(List<PortalItem> items, IQueryCollection query)
    {
        var sortField = ReadFirst(query["sortField"]);
        if (string.IsNullOrWhiteSpace(sortField))
        {
            return items;
        }

        var descending = string.Equals(ReadFirst(query["sortOrder"]), "desc", StringComparison.OrdinalIgnoreCase);
        Comparison<PortalItem> comparison = sortField.ToLowerInvariant() switch
        {
            "created" => static (a, b) => a.Created.CompareTo(b.Created),
            "modified" => static (a, b) => a.Modified.CompareTo(b.Modified),
            "owner" => static (a, b) => string.Compare(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase),
            _ => static (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase),
        };

        items.Sort(comparison);
        if (descending)
        {
            items.Reverse();
        }

        return items;
    }

    private static string ResolveUsername(ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name
            ?? "user";

    private static string ResolveDisplayName(ClaimsPrincipal principal)
        => principal.FindFirstValue("name")
            ?? principal.Identity?.Name
            ?? ResolveUsername(principal);

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
            string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static async Task<GenerateTokenInputs> ReadParametersAsync(HttpContext context)
    {
        string? username;
        string? password;
        string? clientRaw;
        string? refererInput;
        string? expirationRaw;
        string? format;

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            username = ReadFirst(form["username"]);
            password = ReadFirst(form["password"]);
            clientRaw = ReadFirst(form["client"]);
            refererInput = ReadFirst(form["referer"]);
            expirationRaw = ReadFirst(form["expiration"]);
            format = ReadFirst(form["f"]);
        }
        else
        {
            username = ReadFirst(context.Request.Query["username"]);
            password = ReadFirst(context.Request.Query["password"]);
            clientRaw = ReadFirst(context.Request.Query["client"]);
            refererInput = ReadFirst(context.Request.Query["referer"]);
            expirationRaw = ReadFirst(context.Request.Query["expiration"]);
            format = ReadFirst(context.Request.Query["f"]);
        }

        var clientType = ParseClientType(clientRaw);

        int? expirationMinutes = null;
        if (!string.IsNullOrWhiteSpace(expirationRaw) &&
            int.TryParse(expirationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            expirationMinutes = parsed;
        }

        var formatValid = string.IsNullOrWhiteSpace(format) ||
            string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

        return new GenerateTokenInputs(
            username,
            password,
            clientType,
            refererInput,
            expirationMinutes,
            format,
            formatValid);
    }

    private static string? ReadFirst(Microsoft.Extensions.Primitives.StringValues values)
    {
        var value = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static PortalTokenClientType ParseClientType(string? raw)
    {
        if (string.Equals(raw, "ip", StringComparison.OrdinalIgnoreCase))
        {
            return PortalTokenClientType.Ip;
        }

        // Per the ArcGIS spec, "referer" is the default when omitted; we mirror
        // that so existing clients continue to work without sending the
        // parameter.
        return PortalTokenClientType.Referer;
    }

    private static string? ResolveBinding(HttpContext context, PortalTokenClientType clientType, string? refererInput)
    {
        switch (clientType)
        {
            case PortalTokenClientType.Ip:
                var ip = context.Connection.RemoteIpAddress;
                if (ip is null || ip.Equals(IPAddress.None))
                {
                    return null;
                }
                return ip.ToString();
            case PortalTokenClientType.Referer:
            default:
                if (!string.IsNullOrWhiteSpace(refererInput))
                {
                    return refererInput.Trim();
                }
                var headerReferer = context.Request.Headers.Referer.FirstOrDefault();
                return string.IsNullOrWhiteSpace(headerReferer) ? null : headerReferer.Trim();
        }
    }

    private static int ResolveExpirationMinutes(int? requested, PortalTokenAuthenticationOptions settings)
    {
        var defaultMinutes = settings.DefaultExpirationMinutes > 0
            ? settings.DefaultExpirationMinutes
            : PortalTokenAuthenticationOptions.DefaultExpirationMinutesValue;
        var maxMinutes = settings.MaxExpirationMinutes > 0
            ? settings.MaxExpirationMinutes
            : PortalTokenAuthenticationOptions.DefaultMaxExpirationMinutesValue;

        if (requested is null)
        {
            return Math.Min(defaultMinutes, maxMinutes);
        }

        return Math.Min(requested.Value, maxMinutes);
    }

    private static bool IsHttpsRequest(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return true;
        }

        // ASP.NET Core only honors X-Forwarded-Proto via the trusted
        // ForwardedHeaders middleware, so by the time we read Request.IsHttps
        // here it reflects the real (or trusted-forwarded) scheme. No
        // additional header inspection is required.
        return false;
    }

    private readonly record struct GenerateTokenInputs(
        string? Username,
        string? Password,
        PortalTokenClientType ClientType,
        string? RefererInput,
        int? ExpirationMinutes,
        string? Format,
        bool FormatValid);
}

internal sealed partial class SharingRestLog
{
    [LoggerMessage(EventId = 7120, Level = LogLevel.Warning,
        Message = "Portal OAuth authorize rejected: redirect_uri is not registered in the deployment allow-list.")]
    public static partial void OAuthRedirectUriRejected(ILogger logger);
}
