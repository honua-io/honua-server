// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesAdmissionErrorTests : IClassFixture<OgcFeaturesAdmissionErrorTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public WebAppFixture App { get; }
        public Feature[]? StreamingFeatures { get; set; }
        public int StreamStarts { get; private set; }
        public int StreamDisposals { get; private set; }

        public Fixture() => App = Build();

        private WebAppFixture Build()
        {
            var reader = Substitute.For<IFeatureReader, IPagedFeatureReader, IStreamingFeatureStore>();
            var failure = new ServiceUnavailableException("Private pool diagnostics must not escape", 7);
            reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Feature?>(failure));
            reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<QueryResult<Feature>>(failure));
            reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(300L));
            ((IPagedFeatureReader)reader).QueryPageAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<PagedQueryResult<Feature>>(failure));
            ((IStreamingFeatureStore)reader).StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => StreamFeatures(call.ArgAt<CancellationToken>(2)));
            var provider = Substitute.For<IFeatureDataProvider>();
            provider.ProviderName.Returns(DataProviderNames.Postgis);
            provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
            provider.Reader.Returns(reader);

            return new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureServices(services =>
            {
                services.AddSingleton<IFeatureReader>(reader);
                services.AddSingleton<IStreamingFeatureStore>((IStreamingFeatureStore)reader);
                services.AddSingleton<IFeatureDataProviderRegistry>(new FeatureDataProviderRegistry([provider]));
            });
        }

        private async IAsyncEnumerable<Feature> StreamFeatures([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamStarts++;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (StreamingFeatures is not { } features)
                {
                    await Task.FromException(new ServiceUnavailableException("Private pool diagnostics must not escape", 7));
                    yield break;
                }

                foreach (var feature in features)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return feature;
                }
            }
            finally
            {
                StreamDisposals++;
            }
        }

        public Task InitializeAsync() => App.InitializeAsync();
        public Task DisposeAsync() => App.DisposeAsync();
    }

    private readonly WebAppFixture _fixture;
    private readonly Fixture _state;

    public OgcFeaturesAdmissionErrorTests(Fixture fixture)
    {
        _fixture = fixture.App;
        _state = fixture;
    }

    [IntegrationTheory]
    [InlineData("/ogc/features/collections/0/items?limit=1")]
    [InlineData("/ogc/features/collections/0/items/1")]
    [Operation(Operations.Query, Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task FeatureRead_WhenAdmissionUnavailable_Preserves503AndRetryAfter(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, content);
        (response.Headers.RetryAfter?.Delta).Should().Be(TimeSpan.FromSeconds(7));
        (response.Content.Headers.ContentType?.MediaType).Should().Be("application/problem+json");
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        document.RootElement.GetProperty("title").GetString().Should().Be("Service Unavailable");
        content.Should().NotContain("Private pool diagnostics");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task StreamingItems_WhenFirstAdmissionFails_ReturnsCompleteProblemDetails()
    {
        var starts = _state.StreamStarts;
        var disposals = _state.StreamDisposals;
        using var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items?limit=201&f=geojson");
        var content = await response.Content.ReadAsStringAsync();

        _state.StreamStarts.Should().Be(starts + 1, "COUNT succeeds before the streaming connection is acquired");
        _state.StreamDisposals.Should().Be(disposals + 1);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, content);
        (response.Headers.RetryAfter?.Delta).Should().Be(TimeSpan.FromSeconds(7));
        (response.Content.Headers.ContentType?.MediaType).Should().Be("application/problem+json");
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        content.Should().NotContain("Private pool diagnostics");
    }

    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task StreamingItems_EmptyOrSingleRow_PreservesRowsAndDisposesSource(int count)
    {
        var starts = _state.StreamStarts;
        var disposals = _state.StreamDisposals;
        _state.StreamingFeatures = count == 0 ? [] : [Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty)];
        try
        {
            using var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items?limit=201&f=geojson");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var document = JsonDocument.Parse(content);
            document.RootElement.GetProperty("features").GetArrayLength().Should().Be(count);
            document.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(count);
            _state.StreamStarts.Should().Be(starts + 1);
            _state.StreamDisposals.Should().Be(disposals + 1);
        }
        finally
        {
            _state.StreamingFeatures = null;
        }
    }
}
