// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    private static MetadataV2GraphSnapshot BuildGraph(int storageLayerId, string publicationTenant)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "service" },
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "resource" },
            StorageBindingIds = ["binding"],
            PrimaryStorageBindingId = "binding",
            AccessPolicy = new AccessPolicy { AllowedRoles = ["reader"] },
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding", Name = "binding" },
            ResourceId = resource.Metadata.Id,
            StorageLayerId = storageLayerId,
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
