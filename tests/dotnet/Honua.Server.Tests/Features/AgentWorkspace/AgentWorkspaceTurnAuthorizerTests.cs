// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceTurnAuthorizerTests
{
    [UnitTest]
    public async Task NativeTurnRequiresCurrentMembershipTenantTargetAndLayerAccess()
    {
        var principal = Principal();
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var layers = Substitute.For<ILayerAccessAuthorizer>();
        layers.AuthorizeLayerAsync(principal, 7, AuthorizationOperation.Metadata, Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        using var services = new ServiceCollection().AddSingleton(tenant).AddSingleton(layers)
            .AddSingleton(authorization).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services, User = principal };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AgentWorkspace:ReleaseId"] = "release-1" }).Build();
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            OwnerId = "human-1",
            Members = [actor],
            TenantId = "tenant-1",
        };
        document.Views["tab-1"] = [new("workspace-1", "target-1", "tab-1", 0, [new("layer", "7")], null, "tenant-1")];
        document = AgentWorkspaceState.AddTurn(document, "human-1", new("turn-1", "tab-1", 0, "Style this layer"));
        var store = Substitute.For<IAgentWorkspaceStore>();
        store.GetTargetIdAsync().Returns("target-1");
        store.GetAsync("workspace-1").Returns(document);
        var authorizer = new AgentWorkspaceTurnAuthorizer(store, new HttpContextAccessor { HttpContext = http }, configuration);
        var binding = await authorizer.AuthorizeAsync(principal, "workspace-1", "turn-1");
        binding.Should().NotBeNull();
        binding!.TargetId.Should().Be("target-1");
        binding.TenantId.Should().Be("tenant-1");
        binding.ReleaseId.Should().Be("release-1");

        layers.AuthorizeLayerAsync(principal, 7, AuthorizationOperation.Metadata, Arg.Any<CancellationToken>()).Returns(AccessDecision.Forbidden());
        (await authorizer.AuthorizeAsync(principal, "workspace-1", "turn-1")).Should().BeNull();
        layers.AuthorizeLayerAsync(principal, 7, AuthorizationOperation.Metadata, Arg.Any<CancellationToken>()).Returns(AccessDecision.Allowed());
        tenant.TenantId.Returns("tenant-2");
        (await authorizer.AuthorizeAsync(principal, "workspace-1", "turn-1")).Should().BeNull();
        tenant.TenantId.Returns("tenant-1");
        store.GetTargetIdAsync().Returns("target-2");
        (await authorizer.AuthorizeAsync(principal, "workspace-1", "turn-1")).Should().BeNull();
        store.GetTargetIdAsync().Returns("target-1");
        store.GetAsync("workspace-1").Returns(document with { Members = ["different-agent"] });
        (await authorizer.AuthorizeAsync(principal, "workspace-1", "turn-1")).Should().BeNull();
    }

    [UnitTest]
    public async Task HumanCredentialOrMissingNativeGrantCannotBorrowWorkspaceAuthority()
    {
        var store = Substitute.For<IAgentWorkspaceStore>();
        var configuration = new ConfigurationBuilder().Build();
        foreach (var principal in new[] { Principal(false), new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human")], "Bearer")) })
        {
            var authorizer = new AgentWorkspaceTurnAuthorizer(store,
                new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, configuration);
            (await authorizer.AuthorizeAsync(principal, "workspace-1", "turn-1")).Should().BeNull();
        }
        await store.DidNotReceive().GetAsync(Arg.Any<string>());
    }

    private static ClaimsPrincipal Principal(bool native = true)
    {
        var identity = new ClaimsIdentity([
            new Claim("api_key_id", "00000000-0000-0000-0000-000000000017"),
            new Claim("permission", "admin:read"),
        ], AuthenticationExtensions.ApiKeyScheme);
        if (native) identity.AddClaim(new Claim("permission", "agent:workspace"));
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        return new(identity);
    }
}
