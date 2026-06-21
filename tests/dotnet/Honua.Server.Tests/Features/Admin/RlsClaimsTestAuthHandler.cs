// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Header-driven test authentication handler for the row-level security suite (#502).
/// Reads a user name, optional roles, and an optional <c>category</c> claim from request
/// headers so a test can drive RLS resolution with arbitrary claim values. When no user
/// header is present the request stays anonymous (so anonymous-rejection tests still work).
/// </summary>
internal sealed class RlsClaimsTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RlsTest";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";
    public const string CategoryHeader = "X-Test-Category";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var userName = userValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userName),
            new(ClaimTypes.Name, userName),
        };

        if (Request.Headers.TryGetValue(RolesHeader, out var rolesValues))
        {
            foreach (var role in rolesValues.ToString()
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("roles", role));
            }
        }

        // Multiple category values are supported via repeated header values, so an
        // IN-style policy can match any of the caller's categories.
        if (Request.Headers.TryGetValue(CategoryHeader, out var categoryValues))
        {
            foreach (var value in categoryValues)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    claims.Add(new Claim("category", value));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
