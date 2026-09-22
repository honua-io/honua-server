// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Read-policy parity across storage providers (SEC-3): the row-level security and field-mask
/// policy create handlers refuse a policy that targets a layer served by a provider that cannot
/// enforce it, and startup refuses a policy store registered without its enforcement source.
/// The handlers are exercised directly so no database or host is required.
/// </summary>
public sealed class RowLevelSecurityPolicyEnforceabilityTests
{
    private const string WarehouseConnectionId = "conn-warehouse";

    [Fact]
    public async Task CreateRlsPolicy_LayerOnProviderWithoutEnforcement_ReturnsBadRequestAndStoresNothing()
    {
        var store = new InMemoryRlsPolicyStore();

        var result = await RlsPolicyEndpoints.HandleCreatePolicy(
            CreateRlsRequest(layer: "roads"),
            store,
            CreateChecker(),
            NullLogger<RlsPolicyEndpoints.RlsPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        var badRequest = result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>().Subject;
        badRequest.Value!.Success.Should().BeFalse();
        badRequest.Value.Message.Should().Contain("'roads'").And.Contain("'duckdb'").And.Contain("row-level security");
        (await store.ListPoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRlsPolicy_WildcardScopeCoveringSuchALayer_ReturnsBadRequest()
    {
        var result = await RlsPolicyEndpoints.HandleCreatePolicy(
            CreateRlsRequest(layer: "*"),
            new InMemoryRlsPolicyStore(),
            CreateChecker(),
            NullLogger<RlsPolicyEndpoints.RlsPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>();
    }

    [Fact]
    public async Task CreateRlsPolicy_LayerOnEnforcingProvider_IsCreated()
    {
        var store = new InMemoryRlsPolicyStore();

        var result = await RlsPolicyEndpoints.HandleCreatePolicy(
            CreateRlsRequest(layer: "parcels"),
            store,
            CreateChecker(),
            NullLogger<RlsPolicyEndpoints.RlsPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        result.Result.Should().BeOfType<Created<ApiResponse<RlsPolicyResponse>>>();
        (await store.ListPoliciesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateFieldMaskPolicy_LayerOnProviderWithoutEnforcement_ReturnsBadRequestAndStoresNothing()
    {
        var store = new InMemoryFieldMaskPolicyStore();

        var result = await FieldMaskPolicyEndpoints.HandleCreatePolicy(
            CreateFieldMaskRequest(layer: "roads"),
            store,
            CreateChecker(),
            NullLogger<FieldMaskPolicyEndpoints.FieldMaskPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        var badRequest = result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>().Subject;
        badRequest.Value!.Success.Should().BeFalse();
        badRequest.Value.Message.Should().Contain("'roads'").And.Contain("'duckdb'").And.Contain("field-mask");
        (await store.ListPoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFieldMaskPolicy_LayerOnEnforcingProvider_IsCreated()
    {
        var store = new InMemoryFieldMaskPolicyStore();

        var result = await FieldMaskPolicyEndpoints.HandleCreatePolicy(
            CreateFieldMaskRequest(layer: "parcels"),
            store,
            CreateChecker(),
            NullLogger<FieldMaskPolicyEndpoints.FieldMaskPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        result.Result.Should().BeOfType<Created<ApiResponse<FieldMaskPolicyResponse>>>();
        (await store.ListPoliciesAsync()).Should().ContainSingle();
    }

    [Fact]
    public void StartupValidation_PolicyStoresWithTheirSources_Passes()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IRlsPolicyStore, InMemoryRlsPolicyStore>()
            .AddScoped(_ => Mock.Of<IRowLevelSecurityFilterSource>())
            .AddScoped<IFieldMaskPolicyStore, InMemoryFieldMaskPolicyStore>()
            .AddScoped(_ => Mock.Of<IFieldMaskSource>())
            .BuildServiceProvider();

        var act = () => ReadPolicyWiringStartupValidator.Validate(provider.GetRequiredService<IServiceProviderIsService>());

        act.Should().NotThrow();
    }

    [Fact]
    public void StartupValidation_NoPolicyStores_Passes()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => ReadPolicyWiringStartupValidator.Validate(provider.GetRequiredService<IServiceProviderIsService>());

        act.Should().NotThrow();
    }

    [Fact]
    public void StartupValidation_RlsPolicyStoreWithoutRowFilterSource_Throws()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IRlsPolicyStore, InMemoryRlsPolicyStore>()
            .BuildServiceProvider();

        var act = () => ReadPolicyWiringStartupValidator.Validate(provider.GetRequiredService<IServiceProviderIsService>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*IRowLevelSecurityFilterSource*");
    }

    [Fact]
    public async Task StartupValidation_FieldMaskPolicyStoreWithoutFieldMaskSource_FailsHostedServiceStart()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IFieldMaskPolicyStore, InMemoryFieldMaskPolicyStore>()
            .BuildServiceProvider();
        var validator = new ReadPolicyWiringStartupValidator(provider.GetRequiredService<IServiceProviderIsService>());

        var act = () => validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*IFieldMaskSource*");
    }

    private static CreateRlsPolicyRequest CreateRlsRequest(string layer) => new()
    {
        Role = "analyst",
        Service = "*",
        Layer = layer,
        Attribute = "region",
        ClaimType = "region"
    };

    private static CreateFieldMaskPolicyRequest CreateFieldMaskRequest(string layer) => new()
    {
        Role = "analyst",
        Service = "*",
        Layer = layer,
        Attribute = "name"
    };

    private static ReadPolicyEnforceabilityChecker CreateChecker()
    {
        var registry = new FeatureDataProviderRegistry(
        [
            CreateProvider(DataProviderNames.Postgis, FeatureProviderCapabilities.ReadWritePostgis),
            CreateProvider(DataProviderNames.DuckDb, FeatureProviderCapabilities.ReadOnlyAnalytical)
        ]);
        var router = new FeatureProviderQueryRouter(Mock.Of<ISecureConnectionRegistry>(), registry);

        return new ReadPolicyEnforceabilityChecker(new StubGraphProvider(CreateSnapshot()), router, registry);
    }

    private static IFeatureDataProvider CreateProvider(string providerName, FeatureProviderCapabilities capabilities)
    {
        var provider = new Mock<IFeatureDataProvider>(MockBehavior.Strict);
        provider.SetupGet(dataProvider => dataProvider.ProviderName).Returns(providerName);
        provider.SetupGet(dataProvider => dataProvider.Capabilities).Returns(capabilities);
        return provider.Object;
    }

    private static MetadataV2GraphSnapshot CreateSnapshot()
    {
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [CreateResource("res-parcels", "parcels", "binding-parcels"), CreateResource("res-roads", "roads", "binding-roads")],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = WarehouseConnectionId, Name = "warehouse" },
                    Provider = DataProviderNames.DuckDb
                }
            ],
            StorageBindings =
            [
                CreateBinding("binding-parcels", "res-parcels", connectionId: null, storageLayerId: 1),
                CreateBinding("binding-roads", "res-roads", WarehouseConnectionId, storageLayerId: 2)
            ]
        };

        return new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);
    }

    private static MetadataV2Resource CreateResource(string id, string name, string bindingId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
        Type = MetadataV2ResourceType.FeatureDataset,
        StorageBindingIds = [bindingId]
    };

    private static MetadataV2StorageBinding CreateBinding(string id, string resourceId, string? connectionId, int storageLayerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        ResourceId = resourceId,
        ConnectionId = connectionId,
        StorageType = MetadataV2StorageType.RelationalTable,
        Locator = "public." + id,
        StorageLayerId = storageLayerId
    };

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }
}
