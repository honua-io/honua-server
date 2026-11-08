// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.ControlPlane;

internal sealed class OperationHumanApprovalTestHost : IDisposable
{
    private readonly TestRequestContextAccessor _requests = new();
    private readonly ServiceProvider _services;

    public OperationHumanApprovalTestHost(
        IDataProtectionProvider? protection = null,
        IDistributedCache? sharedSessions = null,
        string audience = "https://server.example.com")
    {
        Clock = new ApprovalTestClock();
        Permissions = Substitute.For<IPermissionResolver>();
        Permissions.AuthorizeAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<AuthorizationOperation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(PermissionDecision.NoMatch());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Public:BaseUrl"] = audience }).Build());
        services.AddSingleton<IHttpContextAccessor>(_requests);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IDataProtectionProvider>(protection ?? new EphemeralDataProtectionProvider());
        services.AddSingleton(Permissions);
        services.AddSingleton<AdminAuthSessionStore>();
        services.AddSingleton<IClaimsTransformation, NoopClaimsTransformation>();
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>());
        services.AddAuthorization(options => options.AddPolicy(
            AuthenticationExtensions.AdminApprovePolicy, policy => policy.RequireRole("admin")));
        if (sharedSessions is not null) services.AddSingleton(sharedSessions);
        services.AddSingleton<OperationHumanApprovalAuthority>();
        _services = services.BuildServiceProvider();
    }

    public ApprovalTestClock Clock { get; }
    public IPermissionResolver Permissions { get; }
    public HttpContext Context => _requests.HttpContext!;
    public OperationHumanApprovalAuthority Authority => _services.GetRequiredService<OperationHumanApprovalAuthority>();
    public AdminAuthSessionStore Sessions => _services.GetRequiredService<AdminAuthSessionStore>();

    public async Task<ApprovalTestHuman> CreateHumanAsync(
        string subject = "person", AdminAuthSessionOrigin origin = AdminAuthSessionOrigin.InteractiveOidc)
    {
        AdminAuthSessionClaim[] claims =
        [
            new() { Type = "sub", Value = subject },
            new() { Type = "iss", Value = "https://idp.example.com" },
            new() { Type = ClaimTypes.Role, Value = "admin" },
            new() { Type = "amr", Value = "pwd" },
            new() { Type = "sid", Value = "claimed-interactive-session" },
        ];
        var id = await Sessions.CreateAuthenticatedSessionAsync(
            new AdminAuthAuthenticatedSession
            {
                ProviderKey = "oidc", AccessToken = "access-token", IdToken = "id-token", Claims = claims,
                ExpiresAt = Clock.GetUtcNow().AddMinutes(30), Origin = origin
            }, CancellationToken.None);
        return new ApprovalTestHuman(id, AdminAuthClaimsProjector.CreatePrincipal(
            claims, OidcAuthenticationExtensions.AdminSessionScheme));
    }

    public void ReviewRequest(ApprovalTestHuman human) => SetRequest(human, AdminHumanApprovalEndpointMetadata.Review);

    public void ApproveRequest(ApprovalTestHuman human, OperationHumanApprovalReview review)
    {
        SetRequest(human, AdminHumanApprovalEndpointMetadata.Approve);
        Context.Request.Headers[OperationHumanApprovalAuthority.CapabilityHeader] = review.Capability;
    }

    public static OperationProposalApprovalContext Approval(ApprovalTestHuman human) => new()
    {
        ApprovedBy = CanonicalSecurityActor.Resolve(human.Principal)!.ActorId,
        TenantId = "tenant-a",
    };

    public static OperationProposal Proposal(string requester = "agent:requester")
    {
        var plan = new OperationProposalPlan { Summary = "Change a setting", ExecutionPayload = "{}" };
        var policy = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.AdminConfigChange,
            HonuaEdition.Community, "agent-approval-floor");
        var planHash = OperationApprovalPlanSeal.Compute(plan);
        return new OperationProposal
        {
            ProposalId = "proposal-a",
            TenantId = "tenant-a",
            OperationId = LegacyOperationIds.For(OperationClass.AdminConfigChange),
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = requester,
            Status = OperationProposalStatus.AwaitingApproval,
            Version = 3,
            Plan = plan,
            SealedPlanHash = planHash,
            GuardrailDecision = policy,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Initiation = new OperationInitiationEvidence
            {
                Version = OperationInitiationEvidence.CurrentVersion,
                Kind = OperationInitiationKind.Agent,
                Channel = OperationInitiationChannel.NoninteractiveHttp,
                PrincipalId = requester,
                TenantId = "tenant-a",
                PlanHash = planHash,
                PolicyRevision = OperationGateway.ComputePolicyRevision(policy),
                AcceptedAt = DateTimeOffset.UtcNow,
            },
        };
    }

    private void SetRequest(ApprovalTestHuman human, AdminHumanApprovalEndpointMetadata metadata)
    {
        var context = new DefaultHttpContext { RequestServices = _services, User = human.Principal };
        context.Request.Method = metadata == AdminHumanApprovalEndpointMetadata.Review ? "GET" : "POST";
        context.Request.Path = "/api/v1/admin/proposals/proposal-a/" +
            (metadata == AdminHumanApprovalEndpointMetadata.Review ? "approval-review" : "approve");
        context.Request.Headers.Cookie = AdminAuthSessionStore.AuthSessionCookieName + "=" + human.SessionId;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "human approval"));
        _services.GetRequiredService<RequestTenantContext>().Set("tenant-a", TenantContextSource.Claim);
        _requests.HttpContext = context;
    }

    public void Dispose() => _services.Dispose();

    private sealed class TestRequestContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}

internal sealed class ApprovalTestHuman(string sessionId, ClaimsPrincipal principal)
{
    public string SessionId { get; } = sessionId;
    public ClaimsPrincipal Principal { get; } = principal;
}

internal sealed class ApprovalTestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}
