// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

public sealed class SynchronousPublishIdentityTests
{
    private readonly ILayerPublishingService _publishing = Substitute.For<ILayerPublishingService>();
    private readonly IMetadataV2GraphProvider _graphs = Substitute.For<IMetadataV2GraphProvider>();
    private readonly ServicePublishExecutor _executor;

    public SynchronousPublishIdentityTests()
    {
        _publishing.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                StorageLayerId = 321,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "parcels",
                MetadataRevision = 42,
            });
        _executor = new ServicePublishExecutor(
            _publishing, Substitute.For<ISecureConnectionResolver>(), _graphs, TimeProvider.System);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task SubmitAsync_CommittedGraphIndex_ReturnsDistinctStorageAndServiceSelectors(int serviceLayerIndex)
    {
        var snapshot = Graph("parcels", serviceLayerIndex);
        _graphs.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(snapshot);

        var handle = await SubmitAsync();

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.MetadataRevision.Should().Be(42);
        handle.Result!.Details.Should().Contain("metadataRevision", "42");
        handle.Result.Details.Should().Contain("storageLayerId", "321");
        var indexText = serviceLayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        handle.Result.Details.Should().Contain("serviceLayerIndex", indexText).And.NotContainKey("layerId");
        handle.ResourceIds.Should().Contain("serviceLayerIndex", indexText).And.Contain("storageLayerId", "321");
        await _publishing.Received(1).PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_PostCommitGraphReadFails_CompletesWithoutInventedIndex()
    {
        _graphs.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<MetadataV2GraphSnapshot>(_ => throw new InvalidOperationException("unavailable"));

        var handle = await SubmitAsync();

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.MetadataRevision.Should().Be(42);
        handle.Result!.Details.Should().Contain("storageLayerId", "321").And.Contain("readbackStatus", "Unavailable");
        handle.Result.Details.Should().NotContainKey("serviceLayerIndex");
        handle.ResourceIds.Should().NotContainKey("serviceLayerIndex");
        await _publishing.Received(1).PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_PersistedServiceDiffers_OmitsOtherServicesIndex()
    {
        _graphs.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Graph("other-service", 9));

        var handle = await SubmitAsync();

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.Result!.Details.Should().Contain("serviceName", "parcels").And.NotContainKey("serviceLayerIndex");
        handle.ResourceIds.Should().Contain("serviceName", "parcels").And.NotContainKey("serviceLayerIndex");
    }

    private Task<OperationHandle> SubmitAsync() => _executor.SubmitAsync(
        new OperationRequest
        {
            OperationId = "service.publish",
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            ServiceName = "parcels",
            Parameters = new Dictionary<string, string?>
            {
                ["schema"] = "public",
                ["table"] = "parcels",
                ["layerName"] = "Parcels",
            },
        },
        new OperationPolicyContext { ResolvedConnectionString = "Host=localhost;Database=test" });

    private static MetadataV2GraphSnapshot Graph(string serviceName, int index) => new(
        new TestMetadataV2GraphBuilder()
            .WithRevision(99)
            .AddResource("res-parcels", "Parcels")
            .AddStorageBinding("bind-parcels", "res-parcels", "public.parcels", storageLayerId: 321)
            .AddService("svc-parcels", serviceName)
            .AddPublication("pub-parcels", "svc-parcels", "res-parcels",
                layerIndex: index, storageBindingId: "bind-parcels")
            .Build(),
        "\"newer-graph\"", DateTimeOffset.UtcNow);
}
