// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    /// Sets the 401 challenge status without writing a body. The admin authorization
    /// policy lists multiple authentication schemes (composite, client certificate,
    /// operator bearer), and ASP.NET Core challenges each one in turn on an auth
    /// failure. A preceding scheme (e.g. client certificate) may already have written
    /// a problem-details response, so this guards <see cref="HttpResponse.HasStarted"/>
    /// before touching the status code to avoid "the response has already started".
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the 403 forbidden status without writing a body. See
    /// <see cref="HandleChallengeAsync"/> for why <see cref="HttpResponse.HasStarted"/>
    /// is guarded before the status code is set.
    /// </summary>
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
