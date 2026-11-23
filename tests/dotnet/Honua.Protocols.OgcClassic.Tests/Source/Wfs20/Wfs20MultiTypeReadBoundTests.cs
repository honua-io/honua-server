// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

// A GetFeature that reads many published types holds a bounded number of reads at a time, and a
// request that selects its types only through qualified resource ids reads only those types.
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Query)]
[Collection("Database")]
public sealed class Wfs20MultiTypeReadBoundTests : IClassFixture<Wfs20MultiTypeReadBoundTests.Fixture>
{
    private const int PublishedTypeCount = 3 * RequestFanOut.MaxConcurrency;

    private readonly Fixture _fixture;

    public Wfs20MultiTypeReadBoundTests(Fixture fixture)
    {
        _fixture = fixture;
        _fixture.Counts.Reset();
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_WithoutTypeNames_CountsPublishedTypesWithBoundedConcurrency()
    {
        using var response = await _fixture.App.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&RESULTTYPE=hits");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _fixture.Counts.LayerIds.Should().HaveCount(PublishedTypeCount);
        _fixture.Counts.MaxActive.Should().BeLessOrEqualTo(RequestFanOut.MaxConcurrency);
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_QualifiedResourceIdsWithoutTypeNames_CountsOnlyNamedTypes()
    {
        using var response = await _fixture.App.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&RESULTTYPE=hits&RESOURCEID=bound_type_3.1,bound_type_5.2");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _fixture.Counts.LayerIds.Order().Should().Equal(3, 5);
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_ResourceIdNamingNoPublishedType_KeepsFullSelection()
    {
        using var response = await _fixture.App.Client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&RESULTTYPE=hits&RESOURCEID=bound_type_3.1,missing_type.2");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _fixture.Counts.LayerIds.Should().HaveCount(PublishedTypeCount);
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_ResourceIdsAllNamingOneType_CountsOnlyThatType()
    {
        await AssertCountedLayersAsync("VERSION=2.0.0&RESOURCEID=bound_type_3.1,bound_type_3.2", [3]);
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_ResourceIdListWithoutIds_KeepsFullSelection()
    {
        await AssertCountsEveryTypeAsync("VERSION=2.0.0&RESOURCEID=%2C");
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_UnqualifiedResourceId_KeepsFullSelection()
    {
        await AssertCountsEveryTypeAsync("VERSION=2.0.0&RESOURCEID=bound_type_3.1,7");
    }

    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_LegacyFeatureIdsWithoutTypeName_CountsOnlyNamedTypes()
    {
        await AssertCountedLayersAsync("VERSION=1.1.0&FEATUREID=bound_type_3.1,bound_type_5.2", [3, 5]);
    }

    private async Task AssertCountsEveryTypeAsync(string query)
    {
        await SendHitsAsync(query);
        _fixture.Counts.LayerIds.Should().HaveCount(PublishedTypeCount);
    }

    private async Task AssertCountedLayersAsync(string query, int[] expectedLayerIds)
    {
        await SendHitsAsync(query);
        _fixture.Counts.LayerIds.Order().Should().Equal(expectedLayerIds);
    }

    private async Task SendHitsAsync(string query)
    {
        using var response = await _fixture.App.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&RESULTTYPE=hits&{query}");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public CountProbe Counts { get; } = new();

        public WebAppFixture App { get; }

        public Fixture()
        {
            var reader = Substitute.For<IFeatureReader>();
            reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => Counts.CountAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(2)));
            var provider = Substitute.For<IFeatureDataProvider>();
            provider.ProviderName.Returns(DataProviderNames.Postgis);
            provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
            provider.Reader.Returns(reader);

            App = new WebAppFixture()
                .WithTestLicense(HonuaEdition.Pro)
                .ReplaceService<IMetadataV2GraphProvider>(PublishedTypes())
                .ConfigureServices(services =>
                {
                    services.AddSingleton(reader);
                    services.AddSingleton<IFeatureDataProviderRegistry>(new FeatureDataProviderRegistry([provider]));
                });
        }

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();

        private static TestMetadataV2GraphProvider PublishedTypes()
        {
            const string ServiceId = "svc-bound-types";
            var builder = new TestMetadataV2GraphBuilder().AddService(
                ServiceId, "bound_types", protocols: [MetadataV2ServiceProtocols.Wfs20],
                accessPolicy: new AccessPolicy { AllowAnonymous = true });
            for (var layerId = 1; layerId <= PublishedTypeCount; layerId++)
            {
                var name = $"bound_type_{layerId}";
                builder.AddResource("res-" + name, name,
                        fields:
                        [
                            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
                        ],
                        accessPolicy: new AccessPolicy { AllowAnonymous = true },
                        spatial: new MetadataV2ObjectSpatial
                        {
                            SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true },
                            GeometryType = MetadataV2GeometryType.Point
                        })
                    .AddStorageBinding("binding-" + name, "res-" + name, name, storageLayerId: layerId)
                    .AddPublication(name, ServiceId, "res-" + name,
                        layerIndex: layerId, storageBindingId: "binding-" + name,
                        publicationType: MetadataV2LayerType.WfsFeatureType);
            }

            return new TestMetadataV2GraphProvider(builder.Build());
        }
    }

    public sealed class CountProbe
    {
        private readonly ConcurrentQueue<int> _layerIds = new();
        private readonly Lock _sync = new();
        private int _active;
        private int _maxActive;

        public IReadOnlyCollection<int> LayerIds => _layerIds.ToArray();

        public int MaxActive
        {
            get
            {
                lock (_sync)
                {
                    return _maxActive;
                }
            }
        }

        public void Reset()
        {
            _layerIds.Clear();
            lock (_sync)
            {
                _maxActive = 0;
            }
        }

        public async Task<long> CountAsync(int layerId, CancellationToken cancellationToken)
        {
            _layerIds.Enqueue(layerId);
            var active = Interlocked.Increment(ref _active);
            lock (_sync)
            {
                _maxActive = Math.Max(_maxActive, active);
            }

            try
            {
                // Hold the read long enough that every concurrently started count overlaps.
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
