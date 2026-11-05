// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Authentication;

// A missing bearer or a disabled feature returns no result so other schemes can run; a bearer that fails validation fails the request.
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
            if (principal.Identity is ClaimsIdentity identity)
            {
                // Token transport claims are deliberately excluded from the admin-session
                // projection, but MCP session binding still needs the issuer that this
                // concrete handler validated. Reintroduce that validator-owned value after
                // projection; no token/session claim can choose it.
                identity.AddClaim(new Claim("iss", _tokenService.ValidatedIssuer));
            }

            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (ArgumentException)
        {
            return AuthenticateResult.Fail("The operator bearer is invalid.");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
