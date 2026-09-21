// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Policy denials written by the authorization middleware short-circuit the pipeline above
/// <c>UseHonuaAuditLog</c>, so the audit middleware never observes them. These tests drive the
/// authorization result handler directly to prove the 401/403 outcome of an arbitrary policy is
/// recorded there, exactly once.
/// </summary>
public sealed class HonuaAuthorizationMiddlewareResultHandlerTests
{
    private const string RequestPath = "/api/v1/admin/roles/1";

    private readonly CapturingAuditLog _audit = new();

    [Fact]
    public async Task Challenge_OnPolicyWithoutDomainSeam_RecordsAuthenticationFailure()
    {
        var context = BuildContext(authenticated: false);

        await HandleAsync(context, PolicyAuthorizationResult.Challenge());

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.Authentication);
        evt.Action.Should().Be("auth.failure");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
        evt.ActorType.Should().Be(AuditActorType.Anonymous);
        evt.ResourceType.Should().Be("http");
        evt.ResourceId.Should().Be(RequestPath);

        using var details = JsonDocument.Parse(evt.Details);
        details.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status401Unauthorized);
        details.RootElement.GetProperty("method").GetString().Should().Be("DELETE");
    }

    [Fact]
    public async Task Forbid_OnPolicyWithoutDomainSeam_RecordsAuthorizationDenied()
    {
        var context = BuildContext(authenticated: true);

        await HandleAsync(context, PolicyAuthorizationResult.Forbid());

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.Authorization);
        evt.Action.Should().Be("auth.denied");
        evt.Outcome.Should().Be(AuditOutcome.Denied);
        evt.ActorType.Should().Be(AuditActorType.UserId);
        evt.Actor.Should().Be("operator@example.com");

        using var details = JsonDocument.Parse(evt.Details);
        details.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Forbid_WithHandlerReason_RecordsTheStablePolicyCode()
    {
        var context = BuildContext(authenticated: true);
        var failure = AuthorizationFailure.Failed([new AuthorizationFailureReason(
            handler: new PassThroughAuthorizationHandler(),
            message: "admin.permission.denied")]);

        await HandleAsync(context, PolicyAuthorizationResult.Forbid(failure));

        var evt = _audit.Events.Should().ContainSingle().Subject;
        using var details = JsonDocument.Parse(evt.Details);
        details.RootElement.GetProperty("code").GetString().Should().Be("admin.permission.denied");
    }

    [Fact]
    public async Task Forbid_WithControlCharactersInHandlerReason_RecordsSanitizedCode()
    {
        var context = BuildContext(authenticated: true);
        var failure = AuthorizationFailure.Failed([new AuthorizationFailureReason(
            handler: new PassThroughAuthorizationHandler(),
            message: "denied\r\nsecond-line")]);

        await HandleAsync(context, PolicyAuthorizationResult.Forbid(failure));

        var evt = _audit.Events.Should().ContainSingle().Subject;
        using var details = JsonDocument.Parse(evt.Details);
        var code = details.RootElement.GetProperty("code").GetString();
        code.Should().NotContain("\r").And.NotContain("\n");
        code.Should().Be("denied__second-line");
    }

    [Fact]
    public async Task Forbid_WhenDomainSeamAlreadyRecordedTheDecision_RecordsNothing()
    {
        var context = BuildContext(authenticated: true);
        AuditContextResolver.MarkAuthorizationFailureAudited(context);

        await HandleAsync(context, PolicyAuthorizationResult.Forbid());

        _audit.Events.Should().BeEmpty(
            "a decision recorded by a domain seam must not be recorded a second time");
    }

    [Fact]
    public async Task Success_RecordsNothingAndContinuesThePipeline()
    {
        var context = BuildContext(authenticated: true);
        var continued = false;

        var handler = new HonuaAuthorizationMiddlewareResultHandler();
        await handler.HandleAsync(
            _ =>
            {
                continued = true;
                return Task.CompletedTask;
            },
            context,
            BuildPolicy(),
            PolicyAuthorizationResult.Success());

        continued.Should().BeTrue();
        _audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Forbid_OnStudioLifecyclePolicy_RecordsOnlyTheStudioEvent()
    {
        var context = BuildContext(authenticated: true);
        var policy = new AuthorizationPolicyBuilder()
            .AddRequirements(new StudioLifecycleRequirement())
            .Build();

        var handler = new HonuaAuthorizationMiddlewareResultHandler();
        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());

        var evt = _audit.Events.Should().ContainSingle(
            "a policy denial classified by the Studio seam is recorded once, not also as auth.denied").Subject;
        evt.Action.Should().Be("studio.lifecycle");
        evt.Outcome.Should().Be(AuditOutcome.Denied);
    }

    [Fact]
    public async Task Forbid_WhenAuditSinkThrows_StillForbids()
    {
        var context = BuildContext(authenticated: true, auditLog: new ThrowingAuditLog());
        var authentication = context.RequestServices.GetRequiredService<IAuthenticationService>();

        await HandleAsync(context, PolicyAuthorizationResult.Forbid());

        await authentication.Received(1).ForbidAsync(
            context,
            Arg.Any<string>(),
            Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public void AddApiKeyAuthentication_RegistersTheAuditingResultHandler()
    {
        var services = new ServiceCollection();
        services.AddApiKeyAuthentication(new ConfigurationBuilder().Build());

        services.Last(static descriptor => descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler))
            .ImplementationType.Should().Be<HonuaAuthorizationMiddlewareResultHandler>();
    }

    private Task HandleAsync(HttpContext context, PolicyAuthorizationResult result)
    {
        var handler = new HonuaAuthorizationMiddlewareResultHandler();
        return handler.HandleAsync(_ => Task.CompletedTask, context, BuildPolicy(), result);
    }

    private static AuthorizationPolicy BuildPolicy()
        => new AuthorizationPolicyBuilder().RequireAssertion(static _ => true).Build();

    private DefaultHttpContext BuildContext(bool authenticated, IAuditLog? auditLog = null)
    {
        // The default result handler challenges/forbids through IAuthenticationService; a stub
        // keeps these tests on the audit behaviour rather than on the response shaping.
        var authentication = Substitute.For<IAuthenticationService>();
        authentication
            .ChallengeAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<AuthenticationProperties>())
            .Returns(Task.CompletedTask);
        authentication
            .ForbidAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<AuthenticationProperties>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(auditLog ?? _audit);
        services.AddSingleton(authentication);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Method = "DELETE";
        context.Request.Path = RequestPath;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "operator@example.com")],
                authenticationType: "Test"));
        }

        return context;
    }

    private sealed class PassThroughAuthorizationHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = new();

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>("audit-test");
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("audit sink unavailable");
    }
}
