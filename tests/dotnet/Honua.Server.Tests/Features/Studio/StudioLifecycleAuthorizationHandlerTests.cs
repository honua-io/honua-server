// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Studio;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Unit tests for <see cref="StudioLifecycleAuthorizationHandler"/>'s admin-tier admission
/// (honua-server#3001 review follow-ups): the API-key permission grammar must bind exactly the
/// principals the ApiKey scheme authenticated, while OIDC/session admins -- including admins via
/// a configured alias role, and admins whose IdP attached unrelated <c>permission</c> claims --
/// keep the role-based admission the pre-#3001 OIDC-widened admin policies gave them.
/// </summary>
public sealed class StudioLifecycleAuthorizationHandlerTests
{
    private static Task<AuthorizationHandlerContext> EvaluateAsync(
        ClaimsPrincipal principal,
        string httpMethod,
        bool endUserEnabled = false,
        string[]? adminRoleAliases = null)
    {
        var handler = new StudioLifecycleAuthorizationHandler(
            new StaticOptionsMonitor<StudioEndUserAuthorizationOptions>(
                new StudioEndUserAuthorizationOptions { Enabled = endUserEnabled }),
            new StaticOptionsMonitor<AdminRoleOptions>(
                new AdminRoleOptions { AdminRoles = adminRoleAliases ?? ["admin", "administrator"] }),
            new HttpContextAccessor(),
            NullLogger<StudioLifecycleAuthorizationHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = httpMethod;
        var context = new AuthorizationHandlerContext(
            [new StudioLifecycleRequirement()], principal, httpContext);
        return handler.HandleAsync(context).ContinueWith(_ => context, TaskScheduler.Default);
    }

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType));

    [Fact]
    public async Task OidcAdmin_ViaAliasRole_IsAdmittedWithoutPermissionGrammar()
    {
        var principal = Principal(
            "oidc",
            new Claim(ClaimTypes.Role, "administrator"));

        var context = await EvaluateAsync(principal, HttpMethods.Post);

        context.HasSucceeded.Should().BeTrue(
            "an OIDC admin via a configured alias role kept full admin access under the prior policies");
    }

    [Fact]
    public async Task OidcAdmin_WithUnrelatedIdpPermissionClaims_IsNotMisclassifiedAsScopedKey()
    {
        var principal = Principal(
            "oidc",
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("permission", "crm:contacts:write"));

        var context = await EvaluateAsync(principal, HttpMethods.Post);

        context.HasSucceeded.Should().BeTrue(
            "IdP-attached permission claims must not be parsed with the API-key grant grammar");
    }

    [Fact]
    public async Task ApiKeyAdmin_ReadScoped_CanReadButNotMutate()
    {
        var principal = Principal(
            AuthenticationExtensions.ApiKeyScheme,
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("permission", "admin:read"));

        var read = await EvaluateAsync(principal, HttpMethods.Get);
        var write = await EvaluateAsync(principal, HttpMethods.Post);

        read.HasSucceeded.Should().BeTrue("admin:read authorizes safe methods");
        write.HasSucceeded.Should().BeFalse("admin:read must never authorize a mutating method");
    }

    [Fact]
    public async Task NonAdmin_WithFlagOff_IsDenied()
    {
        var principal = Principal("oidc", new Claim(ClaimTypes.NameIdentifier, "user-1"));

        var context = await EvaluateAsync(principal, HttpMethods.Get);

        context.HasSucceeded.Should().BeFalse("end-user mode is off, so only the admin family is admitted");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
