// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Io.Export;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Export;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ExportLayerIdentityTests : IAsyncLifetime
{
    private const int ServiceLayerIndex = 3;
    private const int StorageLayerId = 741;
    private readonly WebAppFixture _fixture = new();
    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader>();
    private readonly IStreamingFeatureStore _streamingStore = Substitute.For<IStreamingFeatureStore>();
    private readonly IExportJobService _jobs = Substitute.For<IExportJobService>();
    private string _serviceName = string.Empty;

    public async Task InitializeAsync()
    {
        _reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(0L);
        _streamingStore.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyFeatureStream());
        _jobs.StartAsync(Arg.Any<ExportJob>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<ExportJob>().JobId));
        _fixture.ReplaceRequestService(_reader).ReplaceRequestService(_streamingStore)
            .ReplaceRequestService(_jobs).ReplaceRequestService(Substitute.For<ICloudFileStorage>());
        await _fixture.InitializeAsync();
        ConfigureLayerIdentity();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_ServiceIndexDiffersFromStorage_StreamsResolvedStorage()
    {
        using var client = _fixture.CreateAdminClient();
        using var response = await client.GetAsync(ExportPath());
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        _streamingStore.Received(1).StreamFeaturesAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        await _reader.Received(1).CountAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_ServiceIndexDiffersFromStorage_QueuesResolvedStorage()
    {
        _reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(50_001L);
        using var client = _fixture.CreateAdminClient();
        using var response = await client.GetAsync(ExportPath());
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
        await _jobs.Received(1).StartAsync(Arg.Is<ExportJob>(job => job.LayerId == StorageLayerId), Arg.Any<CancellationToken>());
        await _reader.Received(1).CountAsync(StorageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    private string ExportPath() =>
        $"/api/v1/admin/services/{Uri.EscapeDataString(_serviceName)}/layers/{ServiceLayerIndex}/export?format=csv";

    private void ConfigureLayerIdentity()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            snapshot.ResolveStorageLayerId(candidate) == WebAppFixture.TestLayerId &&
            snapshot.ResolveResource(candidate)?.Type == MetadataV2ResourceType.FeatureDataset);
        var binding = snapshot.ResolveStorageBinding(publication)!;
        _serviceName = snapshot.Index.ServicesById[publication.ServiceId].Metadata.Name;
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Publications = snapshot.Graph.Publications.Select(candidate => candidate.Metadata.Id == publication.Metadata.Id
                ? candidate with
                {
                    LayerIndex = ServiceLayerIndex,
                    Identifier = new MetadataV2PublicationIdentifier
                    {
                        Value = ServiceLayerIndex.ToString(CultureInfo.InvariantCulture),
                        IsNumeric = true,
                    },
                }
                : candidate).ToArray(),
            StorageBindings = snapshot.Graph.StorageBindings.Select(candidate => candidate.Metadata.Id == binding.Metadata.Id
                ? candidate with { StorageLayerId = StorageLayerId }
                : candidate).ToArray(),
        };
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(graph, schema: _fixture.MetadataGraphSchema);
    }

    private static async IAsyncEnumerable<Feature> EmptyFeatureStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
