// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed class TileOperationProviderRoutingTests
{
    [Theory]
    [InlineData("seed", null, 0)]
    [InlineData("seed", null, 41)]
    [InlineData("seed", "routed", 0)]
    [InlineData("warm", "routed", 0)]
    [InlineData("archive", "routed", 0)]
    [InlineData("publish", "routed", 0)]
    public async Task Execute_SourceBackedLayer_UsesBoundProviderAndStorageId(string operation, string? serviceId, int layerId)
    {
        var fallback = Substitute.For<ITileProvider>();
        var routed = Substitute.For<ITileProvider>();
        routed.GetMvtTileAsync(41, 0, 0, 0, Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(),
                Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
            .Returns([1, 2, 3]);
        using var services = CreateServices(fallback, routed);
        var core = CreateCore(services);
        var request = new TileOperationStartRequest
        {
            Operation = operation, ServiceId = serviceId, LayerId = layerId, MinZoom = 0, MaxZoom = 0, MaxTiles = 1
        };

        var result = await core.ExecuteAsync(
            TileOperationProgress.CreateInitial("routed-job", operation, serviceId, layerId, "WebMercatorQuad"),
            request, services, CancellationToken.None);

        await routed.Received(1).GetMvtTileAsync(41, 0, 0, 0, Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(),
            Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>());
        fallback.ReceivedCalls().Should().BeEmpty();
        if (operation is "seed" or "warm")
        {
            result.Status.Should().Be(OperationStatus.Completed);
        }
        else
        {
            // Generation succeeded; this fixture deliberately stops at the upload boundary.
            result.ArchiveSizeBytes.Should().BeGreaterThan(0);
            result.ErrorMessage.Should().Contain("Cloud storage is not configured");
        }
    }

    [Fact]
    public async Task Execute_RoutedProviderWithoutTiles_RejectsBeforePrimaryProviderIsCalled()
    {
        var fallback = Substitute.For<ITileProvider>();
        using var services = CreateServices(fallback, routed: null);
        var request = new TileOperationStartRequest { Operation = "seed", ServiceId = "routed", LayerId = 0 };

        var act = () => CreateCore(services).ExecuteAsync(
            TileOperationProgress.CreateInitial("unsupported-job", "seed", "routed", 0, "WebMercatorQuad"),
            request, services, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        fallback.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_ServiceWideSeed_ResolvesEachLayerBindingIndependently()
    {
        var fallback = Substitute.For<ITileProvider>();
        var first = Substitute.For<ITileProvider>();
        var second = Substitute.For<ITileProvider>();
        using var services = CreateServices(fallback, first, second);
        var request = new TileOperationStartRequest { Operation = "seed", ServiceId = "routed", MinZoom = 0, MaxZoom = 0 };

        var result = await CreateCore(services).ExecuteAsync(
            TileOperationProgress.CreateInitial("service-job", "seed", "routed", null, "WebMercatorQuad"),
            request, services, CancellationToken.None);

        result.Status.Should().Be(OperationStatus.Completed);
        await first.Received(1).GetMvtTileAsync(41, 0, 0, 0, Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(),
            Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>());
        await second.Received(1).GetMvtTileAsync(42, 0, 0, 0, Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(),
            Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>());
        fallback.ReceivedCalls().Should().BeEmpty();
    }

    private static TileOperationExecutionCore CreateCore(ServiceProvider services) => new(
        Substitute.For<IUniversalProgressStore>(),
        new OutputCacheInvalidationService(null, null, null,
            services.GetRequiredService<IServiceScopeFactory>(), null,
            NullLogger<OutputCacheInvalidationService>.Instance),
        Options.Create(new TileOptions()), Options.Create(new LimitsOptions()), NullLogger.Instance, 100);

    private static ServiceProvider CreateServices(ITileProvider fallback, ITileProvider? routed, ITileProvider? second = null)
    {
        var graphBuilder = new TestMetadataV2GraphBuilder()
            .AddConnection("connection", "routed", provider: DataProviderNames.Postgis)
            .AddResource("resource", "routed-layer")
            .AddStorageBinding("binding", "resource", "public.routed", connectionId: "connection", storageLayerId: 41)
            .AddService("service", "routed")
            .AddPublication("publication", "service", "resource", layerIndex: 0, storageBindingId: "binding");
        if (second is not null)
        {
            graphBuilder.AddResource("resource-2", "other-layer")
                .AddStorageBinding("binding-2", "resource-2", "public.other", connectionId: "connection", storageLayerId: 42)
                .AddPublication("publication-2", "service", "resource-2", layerIndex: 1, storageBindingId: "binding-2");
        }

        var graph = graphBuilder.BuildProvider();
        var provider = routed is null
            ? Substitute.For<IFeatureDataProvider>()
            : Substitute.For<IFeatureDataProvider, IBindableTileProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        if (routed is not null)
        {
            ((IBindableTileProvider)provider).CreateTileProviderForBinding(Arg.Any<FeatureProviderBinding>())
                .Returns(call => ((FeatureProviderBinding)call[0]).StorageBinding.Metadata.Id == "binding-2" ? second! : routed);
        }
        var connections = Substitute.For<ISecureConnectionRegistry>();
        connections.GetConnectionAsync("connection", Arg.Any<CancellationToken>()).Returns(new DataConnection
        {
            ConnectionId = Guid.NewGuid(), Name = "routed", Host = "provider.example.test", Port = 5432,
            DatabaseName = "spatial", Username = "honua", Provider = DataProviderNames.Postgis,
            SecretRef = "env:HONUA_TEST_PROVIDER", SecretType = "environment", CreatedBy = "test"
        });
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(graph);
        services.AddSingleton(fallback);
        services.AddSingleton(new FeatureProviderQueryRouter(connections, new FeatureDataProviderRegistry([provider])));
        services.AddSingleton(Options.Create(new CloudStorageOptions()));
        return services.BuildServiceProvider();
    }
}
