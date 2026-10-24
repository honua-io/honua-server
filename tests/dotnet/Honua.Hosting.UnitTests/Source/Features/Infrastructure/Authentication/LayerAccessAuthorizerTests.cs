// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

public sealed class LayerAccessAuthorizerTests
{
    [Theory]
    [InlineData(true, "tenant-a", "reader", true)]
    [InlineData(false, "tenant-a", "reader", true)]
    [InlineData(true, "tenant-b", "reader", false)]
    [InlineData(false, "tenant-b", "reader", false)]
    [InlineData(true, "tenant-a", "other", false)]
    [InlineData(false, "tenant-a", "other", false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthorizePublicationAsync_DifferentStorageId_EnforcesResourceAndTenantPolicy(
        bool useHttpContext, string publicationTenant, string role, bool expectedAllowed)
    {
        var graph = BuildGraph(storageLayerId: 700, publicationTenant);
        await using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(graph))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-a"),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", "tenant-a"),
        ], "test"));
        var accessor = new HttpContextAccessor
        {
            HttpContext = useHttpContext
                ? new DefaultHttpContext { RequestServices = services, User = principal }
                : null,
        };
        var authorizer = new LayerAccessAuthorizer(
            accessor, services.GetRequiredService<IServiceScopeFactory>());

        var decision = await authorizer.AuthorizePublicationAsync(
            principal,
            graph.Graph.Publications.Single(),
            graph.Graph.Resources.Single(),
            graph.Graph.Services.Single(),
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().Be(expectedAllowed);
    }

    [UnitTest]
    public async Task AuthorizeLayerAsync_ForeignPublicationCannotFallBackToUnscopedResourcePolicy()
    {
        const int storageLayerId = 700;
        var graph = BuildGraph(storageLayerId, publicationTenant: "tenant-b");
        await using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(graph))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-a"),
            new Claim(ClaimTypes.Role, "reader"),
            new Claim("tenant_id", "tenant-a"),
        ], "test"));
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = principal,
        };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var authorizer = new LayerAccessAuthorizer(
            accessor,
            services.GetRequiredService<IServiceScopeFactory>());

        var decision = await authorizer.AuthorizeLayerAsync(
            principal,
            storageLayerId,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeFalse(
            "a foreign publication must be removed before its service grant or the unscoped resource fallback is evaluated");
    }

    [UnitTest]
    public async Task AuthorizeLayerAsync_VisiblePublicationKeepsCoarsePolicyFallback()
    {
        const int storageLayerId = 700;
        var graph = BuildGraph(storageLayerId, publicationTenant: "tenant-a");
        await using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(graph))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-a"),
            new Claim(ClaimTypes.Role, "reader"),
            new Claim("tenant_id", "tenant-a"),
        ], "test"));
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = principal,
        };
        var authorizer = new LayerAccessAuthorizer(
            new HttpContextAccessor { HttpContext = context },
            services.GetRequiredService<IServiceScopeFactory>());

        var decision = await authorizer.AuthorizeLayerAsync(
            principal,
            storageLayerId,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(AuthorizationOperation.Query)]
    [InlineData(AuthorizationOperation.Delete)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthorizeLayerAsync_DraftRestrictedService_KeepsServicePolicy(AuthorizationOperation operation)
    {
        // honua-server#6102: staging a role-restricted service must not widen access to its
        // storage layer by dropping the service policy on the (resource, null) fallback.
        var graph = BuildPublishedGraph(
            serviceLifecycle: MetadataV2LifecycleStatus.Draft,
            publicationLifecycle: MetadataV2LifecycleStatus.Active);

        var decision = await AuthorizeAsync(graph, Principal(), operation);

        decision.IsAllowed.Should().BeFalse(
            "a draft service's AllowedRoles=[hr] must still govern its storage layer");
    }

    [Theory]
    [InlineData(AuthorizationOperation.Query)]
    [InlineData(AuthorizationOperation.Delete)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthorizeLayerAsync_RetiredPublicationUnderLiveRestrictedService_KeepsServicePolicy(
        AuthorizationOperation operation)
    {
        var graph = BuildPublishedGraph(
            serviceLifecycle: MetadataV2LifecycleStatus.Active,
            publicationLifecycle: MetadataV2LifecycleStatus.Retired);

        var decision = await AuthorizeAsync(graph, Principal(), operation);

        decision.IsAllowed.Should().BeFalse(
            "retiring the publication must not drop the live service's AllowedRoles=[hr]");
    }

    [Theory]
    [InlineData(AuthorizationOperation.Query)]
    [InlineData(AuthorizationOperation.Delete)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthorizeLayerAsync_DraftRestrictedService_AdmitsServiceRole(AuthorizationOperation operation)
    {
        var graph = BuildPublishedGraph(
            serviceLifecycle: MetadataV2LifecycleStatus.Draft,
            publicationLifecycle: MetadataV2LifecycleStatus.Active);

        var decision = await AuthorizeAsync(
            graph, Principal(new Claim(ClaimTypes.Role, "hr")), operation);

        decision.IsAllowed.Should().BeTrue("the service role still admits through a draft publication");
    }

    [UnitTest]
    public async Task AuthorizeLayerAsync_DraftPublicationsRequireEveryServiceToAllow()
    {
        // One draft service is open, the other restricted: the open one must not admit,
        // because the restricted service still governs the same storage layer.
        var graph = BuildPublishedGraph(
            serviceLifecycle: MetadataV2LifecycleStatus.Draft,
            publicationLifecycle: MetadataV2LifecycleStatus.Active,
            addOpenDraftService: true);

        var decision = await AuthorizeAsync(graph, Principal(), AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("read:hr-service", true)]
    [InlineData("read:other-service", false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthorizeLayerAsync_DraftPublication_AppliesScopedKeyGovernance(string grant, bool expectedAllowed)
    {
        var graph = BuildPublishedGraph(
            serviceLifecycle: MetadataV2LifecycleStatus.Draft,
            publicationLifecycle: MetadataV2LifecycleStatus.Active);

        var decision = await AuthorizeAsync(
            graph,
            Principal(
                new Claim(ClaimTypes.Role, LayerScopedWriteKey.ScopedKeyRole),
                new Claim("permission", grant)),
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().Be(expectedAllowed);
    }

    [UnitTest]
    public async Task AuthorizeLayerAsync_ResourceWithoutPublications_KeepsResourceOnlyPolicy()
    {
        // Documented behaviour: a never-published storage resource has no service policy to
        // apply, so it is evaluated against its own (here absent) policy only and any
        // authenticated principal is admitted, exactly as before honua-server#6102.
        var graph = BuildPublishedGraph(
            serviceLifecycle: MetadataV2LifecycleStatus.Active,
            publicationLifecycle: MetadataV2LifecycleStatus.Active,
            withPublication: false);

        var decision = await AuthorizeAsync(graph, Principal(), AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeTrue();
    }

    private const int PublishedStorageLayerId = 701;

    private static ClaimsPrincipal Principal(params Claim[] extra)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-a"), .. extra],
            "test"));

    private static async Task<Honua.Core.Features.Security.Domain.AccessDecision> AuthorizeAsync(
        MetadataV2GraphSnapshot graph,
        ClaimsPrincipal principal,
        AuthorizationOperation operation)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(graph))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var authorizer = new LayerAccessAuthorizer(
            new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext { RequestServices = services, User = principal },
            },
            services.GetRequiredService<IServiceScopeFactory>());

        return await authorizer.AuthorizeLayerAsync(principal, PublishedStorageLayerId, operation);
    }

    private static MetadataV2GraphSnapshot BuildPublishedGraph(
        MetadataV2LifecycleStatus serviceLifecycle,
        MetadataV2LifecycleStatus publicationLifecycle,
        bool withPublication = true,
        bool addOpenDraftService = false)
    {
        var restricted = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "hr-service", Name = "hr-service" },
            AccessPolicy = new AccessPolicy { AllowedRoles = ["hr"] },
            Status = new MetadataV2Status { Lifecycle = serviceLifecycle },
        };
        var open = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "open-service", Name = "open-service" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft },
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "staff", Name = "staff" },
            StorageBindingIds = ["staff-binding"],
            PrimaryStorageBindingId = "staff-binding",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "staff-binding", Name = "staff-binding" },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = PublishedStorageLayerId,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        MetadataV2Publication Publish(MetadataV2Service service, MetadataV2LifecycleStatus lifecycle) => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"{service.Metadata.Id}-staff",
                Name = $"{service.Metadata.Id}-staff",
            },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            LayerIndex = 0,
            Status = new MetadataV2Status { Lifecycle = lifecycle },
        };

        List<MetadataV2Service> serviceList = addOpenDraftService ? [open, restricted] : [restricted];
        List<MetadataV2Publication> publicationList = [];
        if (withPublication)
        {
            if (addOpenDraftService)
            {
                publicationList.Add(Publish(open, MetadataV2LifecycleStatus.Active));
            }

            publicationList.Add(Publish(restricted, publicationLifecycle));
        }

        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Revision = 1,
                Services = serviceList,
                Resources = [resource],
                StorageBindings = [binding],
                Publications = publicationList,
            },
            "\"layer-access-draft-tests\"",
            DateTimeOffset.UnixEpoch);
    }

    private static MetadataV2GraphSnapshot BuildGraph(int storageLayerId, string publicationTenant)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "service" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "resource" },
            StorageBindingIds = ["binding"],
            PrimaryStorageBindingId = "binding",
            AccessPolicy = new AccessPolicy { AllowedRoles = ["reader"] },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding", Name = "binding" },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = storageLayerId,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "publication",
                Name = "publication",
                Tenant = publicationTenant,
            },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            LayerIndex = 1,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Revision = 1,
                Services = [service],
                Resources = [resource],
                StorageBindings = [binding],
                Publications = [publication],
            },
            "\"layer-access-tests\"",
            DateTimeOffset.UnixEpoch);
    }

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(
                revision == snapshot.Revision ? snapshot : null);
    }
}
