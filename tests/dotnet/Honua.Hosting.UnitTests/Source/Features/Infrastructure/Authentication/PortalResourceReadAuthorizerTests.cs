// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>Guards live service audiences across canonical read paths and credential/tenant boundaries.</summary>
public sealed class PortalResourceReadAuthorizerTests
{
    [UnitTest]
    public async Task LiveServiceAudience_AlignsSyncAsyncAndProjectorReadDecision_AndRevokes()
    {
        using var harness = await Harness.CreateAsync();
        var authorizer = harness.Services.GetRequiredService<IPortalResourceReadAuthorizer>();
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, harness.Service).IsAllowed.Should().BeTrue();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, harness.Resource,
            harness.Service, AuthorizationOperation.Query)).IsAllowed.Should().BeTrue();
        AccessPolicyHelpers.RequireServiceAccess(harness.Context, harness.Service).Should().BeNull();
        (await AccessPolicyHelpers.RequireServiceAccessAsync(harness.Context, harness.Service,
            AuthorizationOperation.Metadata)).Should().BeNull();
        authorizer.Evaluate(harness.Context.User, harness.Resource, harness.Service).IsAllowed.Should().BeTrue();

        await harness.Groups.RemoveUsersAsync(harness.GroupId, ["member"], CancellationToken.None);
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, harness.Resource,
            harness.Service, AuthorizationOperation.Query)).IsAllowed.Should().BeFalse();
        authorizer.Evaluate(harness.Context.User, harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task Unshare_RevokesLiveReadWithoutChangingMembership()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Sharing.UnshareAsync(harness.Service.Metadata.Id,
            new PortalItemShareRequest(false, false, [harness.GroupId]), CancellationToken.None);
        harness.Groups.GetGroupIdsForUser("member").Should().Contain(harness.GroupId);
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("layer", false)]
    [InlineData("foreign-service", false)]
    [InlineData("foreign-resource", false)]
    [InlineData("wrong-key-service", false)]
    [InlineData("wrong-key-layer", false)]
    [InlineData("nonmember", false)]
    [InlineData("anonymous", false)]
    [InlineData("matching-key", true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ServiceAudience_PreservesResourceTenantAndCredentialGuards(string scenario, bool expected)
    {
        using var harness = await Harness.CreateAsync();
        var service = harness.Service;
        var resource = harness.Resource;
        if (scenario == "layer")
        {
            resource = resource with { AccessPolicy = new AccessPolicy { AllowedRoles = ["layer-reader"] } };
        }
        if (scenario == "foreign-service")
        {
            service = service with { Metadata = service.Metadata with { Tenant = "other-tenant" } };
        }
        if (scenario == "foreign-resource")
        {
            resource = resource with { Metadata = resource.Metadata with { Tenant = "other-tenant" } };
        }
        if (scenario.StartsWith("wrong-key", StringComparison.Ordinal) || scenario == "matching-key")
        {
            var permission = scenario == "wrong-key-service" ? "read:other" : scenario == "wrong-key-layer"
                ? "read:test/other" : "read:test/points";
            harness.Context.User = Principal("member", new Claim(ClaimTypes.Role, LayerScopedWriteKey.ScopedKeyRole),
                new Claim("permission", permission));
        }
        if (scenario == "nonmember")
        {
            harness.Context.User = Principal("stranger");
        }
        if (scenario == "anonymous")
        {
            harness.Context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, resource, service).IsAllowed.Should().Be(expected);
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, resource, service,
            AuthorizationOperation.Query)).IsAllowed.Should().Be(expected);
        harness.Services.GetRequiredService<IPortalResourceReadAuthorizer>()
            .Evaluate(harness.Context.User, resource, service).IsAllowed.Should().Be(expected);
    }

    [UnitTest]
    public async Task ServiceAudience_DoesNotAuthorizeWritesOrAnotherServiceOrForeignPublication()
    {
        using var harness = await Harness.CreateAsync();
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, harness.Service, AccessScope.Write)
            .IsAllowed.Should().BeFalse();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, harness.Resource, harness.Service,
            AuthorizationOperation.Update)).IsAllowed.Should().BeFalse();
        var other = harness.Service with { Metadata = harness.Service.Metadata with { Id = "other-service" } };
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, other).IsAllowed.Should().BeFalse();
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            { Id = "foreign-publication", Name = "foreign-publication", Tenant = "foreign-tenant" }
        };
        harness.Services.GetRequiredService<IPortalResourceReadAuthorizer>()
            .Evaluate(harness.Context.User, harness.Resource, harness.Service, publication).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EveryoneAndOrgOverlay_DoNotGrantServiceAudience()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Sharing.UnshareAsync(harness.Service.Metadata.Id,
            new PortalItemShareRequest(false, false, [harness.GroupId]), CancellationToken.None);
        await harness.Sharing.ShareAsync(harness.Service.Metadata.Id, "owner",
            new PortalItemShareRequest(true, true, []), CancellationToken.None);
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthorizationOperation.Approve)]
    [InlineData((AuthorizationOperation)999)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ServiceAudience_DoesNotAuthorizeApprovalOrUnknownOperation(AuthorizationOperation operation)
    {
        using var harness = await Harness.CreateAsync();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, harness.Resource,
            harness.Service, operation)).IsAllowed.Should().BeFalse();
        (await AccessPolicyHelpers.RequireServiceAccessAsync(harness.Context, harness.Service,
            operation)).Should().NotBeNull();
    }

    [UnitTest]
    public async Task SameSubject_AcrossTenantA_B_AndNull_CannotBorrowGroupMembership()
    {
        using var harness = await Harness.CreateAsync("tenant-a");
        var authorizer = harness.Services.GetRequiredService<IPortalResourceReadAuthorizer>();
        authorizer.Evaluate(harness.Context.User, harness.Resource, harness.Service).IsAllowed.Should().BeTrue();
        foreach (var tenantId in new[] { "tenant-b", null })
        {
            harness.Tenant.TenantId = tenantId;
            harness.Context.User = tenantId is null ? Principal("member")
                : Principal("member", new Claim("tenant_id", tenantId));
            authorizer.Evaluate(harness.Context.User, harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
            (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, harness.Resource,
                harness.Service, AuthorizationOperation.Query)).IsAllowed.Should().BeFalse();
        }
    }

    [UnitTest]
    public async Task TenantAServiceResourceAndPublication_RequireTenantAForTheGroupGrant()
    {
        using var harness = await Harness.CreateAsync("tenant-a");
        var service = harness.Service with { Metadata = harness.Service.Metadata with { Tenant = "tenant-a" } };
        var resource = harness.Resource with { Metadata = harness.Resource.Metadata with { Tenant = "tenant-a" } };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            { Id = "publication", Name = "publication", Tenant = "tenant-a" }
        };
        var authorizer = harness.Services.GetRequiredService<IPortalResourceReadAuthorizer>();
        authorizer.Evaluate(harness.Context.User, resource, service, publication).IsAllowed.Should().BeTrue();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, resource, service,
            AuthorizationOperation.Query)).IsAllowed.Should().BeTrue();
        authorizer.Evaluate(harness.Context.User, resource, service,
            publication with { Metadata = publication.Metadata with { Tenant = "tenant-b" } }).IsAllowed.Should().BeFalse();
        harness.Tenant.TenantId = "tenant-b";
        harness.Context.User = Principal("member", new Claim("tenant_id", "tenant-b"));
        authorizer.Evaluate(harness.Context.User, resource, service, publication).IsAllowed.Should().BeFalse();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(harness.Context, resource, service,
            AuthorizationOperation.Query)).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task MissingRequestContext_AndDisplayOnlyIdentity_DoNotReceiveGroupAudience()
    {
        using var harness = await Harness.CreateAsync();
        var authorizer = harness.Services.GetRequiredService<IPortalResourceReadAuthorizer>();
        harness.Context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "member")], "test"));
        authorizer.Evaluate(harness.Context.User, harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
        harness.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        authorizer.Evaluate(Principal("member"), harness.Resource, harness.Service).IsAllowed.Should().BeFalse();
        authorizer.Evaluate(Principal("member", new Claim(ClaimTypes.Role, "service-reader")),
            harness.Resource, harness.Service).IsAllowed.Should().BeTrue();
        authorizer.Evaluate(Principal("member", new Claim(ClaimTypes.Role, LayerScopedWriteKey.ScopedKeyRole),
            new Claim("permission", "read:test/points")), harness.Resource, harness.Service).IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task LiveGroupReadAudience_DoesNotGrantOperatorProcessExecution()
    {
        using var harness = await Harness.CreateAsync();
        AccessPolicyHelpers.EvaluateResourceAccess(harness.Context, harness.Resource, harness.Service).IsAllowed.Should().BeTrue();
        var evaluator = new OperatorAuthorizationEvaluator(
            harness.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new RbacOptions()),
            NullLogger<OperatorAuthorizationEvaluator>.Instance);
        var decision = await evaluator.EvaluateAsync(harness.Context.User, new OperatorAuthorizationRequest
        {
            ResourceId = "geometry.area",
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute,
        });
        decision.IsAllowed.Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(string name, params Claim[] claims)
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, name) }.Concat(claims), "test"));

    private sealed class Harness : IDisposable
    {
        public ServiceProvider Services { get; private init; } = null!;
        public DefaultHttpContext Context { get; private init; } = null!;
        public InMemoryPortalGroupStore Groups { get; private init; } = null!;
        public InMemoryPortalItemSharingStore Sharing { get; private init; } = null!;
        public string GroupId { get; private init; } = string.Empty;
        public TestTenantContext Tenant { get; private init; } = null!;
        public MetadataV2Service Service { get; } = new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-test", Name = "test", Publisher = "owner" },
            AccessPolicy = new AccessPolicy { AllowedRoles = ["service-reader"], AllowedWriteRoles = ["service-writer"] },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        public MetadataV2Resource Resource { get; } = new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-points", Name = "points" },
            AccessPolicy = new AccessPolicy { AllowedWriteRoles = ["layer-writer"] },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        public static async Task<Harness> CreateAsync(string? tenantId = null)
        {
            var groups = new InMemoryPortalGroupStore();
            var sharing = new InMemoryPortalItemSharingStore();
            var accessor = new TestHttpContextAccessor();
            var tenant = new TestTenantContext { TenantId = tenantId };
            var services = new ServiceCollection()
                .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
                .AddSingleton<IPortalGroupStore>(groups)
                .AddSingleton<IPortalItemSharingStore>(sharing)
                .AddSingleton<IHttpContextAccessor>(accessor)
                .AddSingleton<ITenantContext>(tenant)
                .AddSingleton<IPortalResourceReadAuthorizer, PortalResourceReadAuthorizer>()
                .BuildServiceProvider();
            var principal = tenantId is null ? Principal("member") : Principal("member", new Claim("tenant_id", tenantId));
            var context = new DefaultHttpContext { RequestServices = services, User = principal };
            accessor.HttpContext = context;
            var group = await groups.CreateAsync(new PortalGroupRegistration("owned", null, "private", [], tenantId),
                "owner", CancellationToken.None);
            await groups.AddUsersAsync(group.Id, ["member"], CancellationToken.None);
            var harness = new Harness
            {
                Services = services,
                Context = context,
                Groups = groups,
                Sharing = sharing,
                GroupId = group.Id,
                Tenant = tenant
            };
            await sharing.ShareAsync(harness.Service.Metadata.Id, "owner",
                new PortalItemShareRequest(false, false, [group.Id]), CancellationToken.None);
            return harness;
        }

        public void Dispose()
        {
            Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
            Services.Dispose();
        }
    }
    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public string? TenantId { get; set; }
        public TenantContextSource Source => TenantId is null ? TenantContextSource.Anonymous : TenantContextSource.Claim;
        public bool RequireTenantId(out string tenantId, out string? reason)
        {
            tenantId = TenantId ?? string.Empty;
            reason = TenantId is null ? "No tenant resolved." : null;
            return TenantId is not null;
        }
    }

}
