// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
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

    private const string JsonContentType = "application/json";

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

        PortalTokenLog.TokenIssued(
            logger,
            LogValueRedactor.Hash(verified.PrincipalId),
            LogValueRedactor.Hash(verified.TenantId ?? tenantContext.TenantId ?? string.Empty),
            clientType.ToString(),
            issuance.ExpiresAt);

        var response = new GenerateTokenResponse
        {
            Token = issuance.Token,
            Expires = issuance.ExpiresAt.ToUnixTimeMilliseconds(),
            Ssl = true,
        };

        return Results.Json(response, SharingRestJsonContext.Default.GenerateTokenResponse, contentType: JsonContentType);
    }

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

internal sealed class SharingRestLog
{
}
