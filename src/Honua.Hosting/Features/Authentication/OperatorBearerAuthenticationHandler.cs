// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
        var validation = await _tokenService.TryValidateAsync(token).ConfigureAwait(false);
        if (validation is null)
        {
            return AuthenticateResult.Fail("The operator bearer is invalid or expired.");
        }

        if (validation.Binding.Session is { } binding && !await IsSessionLiveAsync(binding).ConfigureAwait(false))
        {
            return AuthenticateResult.Fail("The operator bearer's session has ended.");
        }

        if (validation.Binding.AdminPasswordVersion is { } version && !await IsAdminPasswordCurrentAsync(version).ConfigureAwait(false))
        {
            return AuthenticateResult.Fail("The administrator password this operator bearer was issued from has changed.");
        }

        return CreateResult(validation, token);
    }

    private AuthenticateResult CreateResult(OperatorBearerValidation validation, string token)
    {
        try
        {
            var principal = AdminAuthClaimsProjector.CreatePrincipal(validation.Claims, Scheme.Name, "operator-bearer");
            if (principal.Identity is ClaimsIdentity identity)
            {
                // Token transport claims are deliberately excluded from the admin-session
                // projection, but MCP session binding still needs the issuer that this
                // concrete handler validated. Reintroduce that validator-owned value after
                // projection; no token/session claim can choose it.
                identity.AddClaim(new Claim("iss", _tokenService.ValidatedIssuer));
            }

            var expiry = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(token).ValidTo;
            Context.Features.Set(StreamCredential(token, validation.Binding, new DateTimeOffset(expiry)));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (ArgumentException)
        {
            return AuthenticateResult.Fail("The operator bearer is invalid.");
        }
    }

    // Fails closed: a session-bound bearer is refused when no session store can confirm the session.
    // A session-bound bearer ends with its session: sign-out deletes the bearer binding token,
    // whose stored hash is the binding's SHA-256.
    internal static LiveStreamCredentialFeature StreamCredential(string token, OperatorBearerBinding binding, DateTimeOffset expiresAt)
        => LiveStreamCredentialFeature.ForSecret("OperatorBearer", token, expiresAt) with
        {
            BackingCredential = binding.Session is { } session
                ? new("AdminSessionBearerBinding", LiveStreamInvalidation.Hash(session)) : null
        };

    private async Task<bool> IsSessionLiveAsync(string binding)
    {
        var sessions = Context.RequestServices.GetService<AdminAuthSessionStore>();
        return sessions is not null
            && await sessions.IsOperatorBearerBindingLiveAsync(binding, Context.RequestAborted).ConfigureAwait(false);
    }

    // Fails closed like the session check: a bearer minted from the administrator password is refused
    // once the password is rotated or when its authority cannot be read, the rule portal tokens minted from the password follow.
    private async Task<bool> IsAdminPasswordCurrentAsync(string version)
    {
        var validator = Context.RequestServices.GetService<IPortalTokenSourceValidator>();
        return validator is not null
            && await validator.IsStillValidAsync(
                new PortalTokenSourceRecord { Kind = PortalCredentialSourceKind.AdminPassword, Version = version },
                Context.RequestAborted).ConfigureAwait(false);
    }

    // Challenge and forbidden handlers guard HasStarted because ASP.NET challenges each scheme in turn and a preceding scheme may already have written the response.
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
