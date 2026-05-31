// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Authenticates requests that carry an ArcGIS-style opaque portal token.
/// </summary>
/// <remarks>
/// ArcGIS clients deliver the token in one of three places: the <c>token</c> query
/// string parameter (legacy/embedded clients), the <c>Authorization: Bearer</c>
/// header, or the <c>X-Esri-Authorization: Bearer</c> header used by newer Esri
/// SDKs. The handler accepts any of these and delegates to
/// <see cref="IPortalTokenIssuer.ValidateAsync"/> for verification.
/// </remarks>
internal sealed class PortalTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPortalTokenIssuer tokenIssuer) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string TokenQueryParameter = "token";
    internal const string BearerPrefix = "Bearer ";
    private const string EsriAuthorizationHeader = "X-Esri-Authorization";

    private readonly IPortalTokenIssuer _tokenIssuer = tokenIssuer ?? throw new ArgumentNullException(nameof(tokenIssuer));

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var binding = new PortalTokenBinding(
            Referer: Request.Headers.Referer.FirstOrDefault(),
            ClientIp: Context.Connection.RemoteIpAddress?.ToString());

        var validation = await _tokenIssuer.ValidateAsync(token, binding, Context.RequestAborted).ConfigureAwait(false);
        if (validation is null)
        {
            return AuthenticateResult.Fail("The supplied portal token is invalid, expired, or bound to a different client.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(validation.Principal, Scheme.Name));
    }

    private string? ExtractToken()
    {
        if (Request.Query.TryGetValue(TokenQueryParameter, out var queryToken) && !StringValues.IsNullOrEmpty(queryToken))
        {
            var candidate = queryToken.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        if (Request.Headers.TryGetValue(EsriAuthorizationHeader, out var esriHeader))
        {
            var fromEsri = TryReadBearerHeader(esriHeader);
            if (!string.IsNullOrEmpty(fromEsri))
            {
                return fromEsri;
            }
        }

        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var fromAuthorization = TryReadBearerHeader(authHeader);
            if (!string.IsNullOrEmpty(fromAuthorization))
            {
                return fromAuthorization;
            }
        }

        return null;
    }

    private static string? TryReadBearerHeader(StringValues headerValues)
    {
        foreach (var value in headerValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = value[BearerPrefix.Length..].Trim();
                if (!string.IsNullOrEmpty(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
