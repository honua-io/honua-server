// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
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
    private static readonly Guid KnownApiKeyId = Guid.Parse("6f1c2d3e-4b5a-4c6d-8e9f-0a1b2c3d4e5f");

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task PolicyDenial_WithUnattestedLineageHeaders_DoesNotRecordForgedAssociations(bool authenticated)
    {
        var context = BuildContext(authenticated);
        context.Request.Headers["X-Honua-Operation-Instance-Id"] = "forged-operation";
        context.Request.Headers["X-Honua-Audit-Id"] = "forged-audit";
        context.Request.Headers["X-Honua-Proposal-Id"] = "forged-proposal";

        await HandleAsync(context, authenticated
            ? PolicyAuthorizationResult.Forbid()
            : PolicyAuthorizationResult.Challenge());

        var evt = _audit.Events.Should().ContainSingle().Subject;
        using var details = JsonDocument.Parse(evt.Details);
        details.RootElement.TryGetProperty("operationInstanceId", out _).Should().BeFalse();
        details.RootElement.TryGetProperty("acceptedAuditId", out _).Should().BeFalse();
        details.RootElement.TryGetProperty("proposalId", out _).Should().BeFalse();
        details.RootElement.GetProperty("status").GetInt32().Should().Be(authenticated ? 403 : 401);
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

    [UnitTheory]
    [InlineData("api-key", "apikey:api-key:6f1c2d3e-4b5a-4c6d-8e9f-0a1b2c3d4e5f", AuditActorType.ApiKey)]
    [InlineData("stamped", "bearer:subject:-:stamped", AuditActorType.UserId)]
    [InlineData("bearer", "bearer:subject:https%3A%2F%2Fissuer.example:subject-1", AuditActorType.UserId)]
    [InlineData("name-only", "test:name:operator", AuditActorType.UserId)]
    // A non-GUID key id is never stamped, so the resolver keeps the raw value.
    [InlineData("non-guid-key", "not-a-guid", AuditActorType.ApiKey)]
    public async Task Forbid_OnStudioLifecyclePolicy_RecordsTheStampedActor(
        string principalCase,
        string expectedActor,
        AuditActorType expectedType)
    {
        // Policy denials run before tenant resolution; they record what it stamps.
        var context = BuildContext(authenticated: false);
        context.User = StudioPrincipal(principalCase);
        var policy = new AuthorizationPolicyBuilder()
            .AddRequirements(new StudioLifecycleRequirement())
            .Build();

        var handler = new HonuaAuthorizationMiddlewareResultHandler();
        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("studio.lifecycle");
        evt.Actor.Should().Be(expectedActor);
        evt.ActorType.Should().Be(expectedType);
    }

    [UnitTest]
    public async Task StudioHandlerDenial_UnstampedApiKey_RecordsCanonicalApiKeyActor()
    {
        // Multi-tenant installs never stamp API-key callers; both Studio audit paths must still agree.
        var context = BuildContext(authenticated: false);
        context.User = ApiKeyPrincipal(KnownApiKeyId.ToString("D"));

        await StudioAuthorizationAudit.RecordDecisionAsync(
            context,
            _audit,
            TimeProvider.System,
            StudioAuthorizationOperation.CreateDraft,
            "studio-content-item",
            null,
            StudioAuthorizationDecision.Deny("studio_authorization/test", "denied"));

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.Actor.Should().Be($"apikey:api-key:{KnownApiKeyId:D}");
        evt.ActorType.Should().Be(AuditActorType.ApiKey);
    }

    [UnitTest]
    public async Task Forbid_OnGenericPolicy_UnstampedApiKey_KeepsTheRawKeyId()
    {
        var context = BuildContext(authenticated: false);
        context.User = ApiKeyPrincipal(KnownApiKeyId.ToString("D"));

        await HandleAsync(context, PolicyAuthorizationResult.Forbid());

        var evt = _audit.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("auth.denied");
        evt.Actor.Should().Be(KnownApiKeyId.ToString("D"));
    }

    private static ClaimsPrincipal StudioPrincipal(string principalCase)
    {
        switch (principalCase)
        {
            case "api-key":
                return ApiKeyPrincipal(KnownApiKeyId.ToString("D"));
            case "non-guid-key":
                return ApiKeyPrincipal("not-a-guid");
            case "stamped":
                var stamped = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "subject-1")],
                    authenticationType: "Bearer");
                var stampedActor = new Claim("honua:canonical_actor", "bearer:subject:-:stamped");
                stampedActor.Properties[CanonicalSecurityActor.FrameworkOwnedClaimProperty] = bool.TrueString;
                stamped.AddClaim(stampedActor);
                return new ClaimsPrincipal(stamped);
            case "bearer":
                return new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", "subject-1"), new Claim("iss", "https://issuer.example")],
                    authenticationType: "Bearer"));
            case "name-only":
                return new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "operator")],
                    authenticationType: "Test"));
            default:
                throw new ArgumentOutOfRangeException(nameof(principalCase), principalCase, null);
        }
    }

    private static ClaimsPrincipal ApiKeyPrincipal(string apiKeyId)
        => new(new ClaimsIdentity(
            [new Claim("api_key_id", apiKeyId), new Claim(ClaimTypes.Name, "carol")],
            authenticationType: AuthenticationExtensions.ApiKeyScheme));

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
