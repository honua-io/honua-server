// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Validates the console-consumable operator bearer (#2258, Option C) on the
/// admin/control-plane request path and projects it into the same principal the
/// cookie session produces, so RBAC resolves identically (see
/// <see cref="AdminAuthClaimsProjector"/>).
/// </summary>
/// <remarks>
/// Fail-closed: when the feature is disabled or the request carries no bearer the
/// handler returns <see cref="AuthenticateResult.NoResult"/> (other schemes may run);
/// when a bearer is present but fails signature/issuer/audience/lifetime validation
/// the handler fails so the request is never treated as authenticated.
/// </remarks>
internal sealed class OperatorBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OperatorBearerTokenService tokenService) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    private readonly OperatorBearerTokenService _tokenService = tokenService
        ?? throw new ArgumentNullException(nameof(tokenService));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_tokenService.Enabled)
        {
            return AuthenticateResult.NoResult();
        }

        var header = Request.Headers[HeaderNames.Authorization].ToString();
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[BearerPrefix.Length..].Trim();
        var claims = await _tokenService.TryValidateAsync(token).ConfigureAwait(false);
        if (claims is null)
        {
            return AuthenticateResult.Fail("The operator bearer is invalid or expired.");
        }

        try
        {
            var principal = AdminAuthClaimsProjector.CreatePrincipal(claims, Scheme.Name, "operator-bearer");
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (ArgumentException)
        {
            return AuthenticateResult.Fail("The operator bearer is invalid.");
        }
    }
}
