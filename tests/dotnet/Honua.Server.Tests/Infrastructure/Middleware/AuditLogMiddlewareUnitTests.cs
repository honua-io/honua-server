// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Unit tests that drive <see cref="AuditLogMiddleware"/> directly to prove the
/// authentication / authorization and login emission branches without standing up
/// the full host (which bypasses auth in the integration harness) (#507).
/// </summary>
public sealed class AuditLogMiddlewareUnitTests
{
    private readonly CapturingAuditLog _audit = new();

    [Fact]
    public async Task FailedLogin_OnAnyRoute_EmitsAuthFailure()
    {
        var context = BuildContext(
            method: "GET",
            routePattern: "/api/v1/admin/config",
            authenticated: false);

        await InvokeAsync(context, finalStatus: StatusCodes.Status401Unauthorized);

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.Authentication);
        evt.Action.Should().Be("auth.failure");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
        evt.ActorType.Should().Be(AuditActorType.Anonymous);
    }

    [Fact]
    public async Task PermissionDenied_OnAnyRoute_EmitsAuthorizationDenied()
    {
        var context = BuildContext(
            method: "DELETE",
            routePattern: "/api/v1/admin/roles/{id}",
            authenticated: true);

        await InvokeAsync(context, finalStatus: StatusCodes.Status403Forbidden);

        // The admin-action descriptor matches the route, but the 403 outcome wins.
        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.Outcome.Should().Be(AuditOutcome.Denied);
        evt.ActorType.Should().Be(AuditActorType.UserId);
    }

    [Fact]
    public async Task SuccessfulLogin_OnTokenRoute_EmitsAuthLoginSuccess()
    {
        var context = BuildContext(
            method: "POST",
            routePattern: "/api/v{version:apiVersion}/admin/auth/providers/{providerKey}/token",
            authenticated: true);

        await InvokeAsync(context, finalStatus: StatusCodes.Status200OK);

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.Authentication);
        evt.Action.Should().Be("auth.login");
        evt.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task FailedLogin_OnTokenRoute_EmitsAuthLoginFailure()
    {
        var context = BuildContext(
            method: "POST",
            routePattern: "/api/v{version:apiVersion}/admin/auth/providers/{providerKey}/token",
            authenticated: false);

        await InvokeAsync(context, finalStatus: StatusCodes.Status401Unauthorized);

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("auth.login");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task NonAuditedRoute_Success_EmitsNothing()
    {
        var context = BuildContext(
            method: "GET",
            routePattern: "/healthz/live",
            authenticated: false);

        await InvokeAsync(context, finalStatus: StatusCodes.Status200OK);

        _audit.Events.Should().BeEmpty();
    }

    private async Task InvokeAsync(HttpContext context, int finalStatus)
    {
        var middleware = new AuditLogMiddleware(
            next: ctx =>
            {
                ctx.Response.StatusCode = finalStatus;
                return Task.CompletedTask;
            },
            actionResolver: new DefaultAuditActionResolver());

        await middleware.InvokeAsync(context);
    }

    private DefaultHttpContext BuildContext(string method, string routePattern, bool authenticated)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLog>(_audit);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Method = method;
        context.Request.Path = "/" + routePattern.TrimStart('/').Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "operator@example.com") },
                authenticationType: "Test"));
        }

        var endpoint = new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse(routePattern),
            order: 0,
            metadata: new EndpointMetadataCollection(),
            displayName: routePattern);
        context.SetEndpoint(endpoint);

        return context;
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
