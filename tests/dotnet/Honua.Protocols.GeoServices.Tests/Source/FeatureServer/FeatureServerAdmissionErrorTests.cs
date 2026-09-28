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

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerAdmissionErrorTests : IClassFixture<FeatureServerAdmissionErrorTests.Fixture>
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
            reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<QueryResult<Feature>>(failure));
            reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<long>(failure));
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

    public FeatureServerAdmissionErrorTests(Fixture fixture)
    {
        _fixture = fixture.App;
        _state = fixture;
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task Query_WhenAdmissionUnavailable_Preserves503ErrorAndRetryAfter(bool serviceQuery)
    {
        var queryPath = serviceQuery ? "query" : $"{WebAppFixture.TestLayerId}/query";
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{queryPath}?where=1=1&resultRecordCount=1&f=json");
        var content = await response.Content.ReadAsStringAsync();

        // GeoServices transports ordinary protocol errors as HTTP 200 envelopes.
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(7));
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(503);
        document.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("error").GetProperty("retryAfterSeconds").GetInt32().Should().Be(7);
        content.Should().NotContain("Private pool diagnostics");
    }

    [IntegrationTheory]
    [InlineData("json", false)]
    [InlineData("geojson", false)]
    [InlineData("json", true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task StreamingQuery_WhenFirstAdmissionFails_ReturnsCompleteErrorEnvelope(string format, bool idsOnly)
    {
        var starts = _state.StreamStarts;
        var disposals = _state.StreamDisposals;
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=1001&f={format}&returnIdsOnly={idsOnly.ToString().ToLowerInvariant()}");
        var content = await response.Content.ReadAsStringAsync();

        _state.StreamStarts.Should().Be(starts + 1, "the regression must exercise streaming admission");
        _state.StreamDisposals.Should().Be(disposals + 1);
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(7));
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(503);
        content.Should().NotContain("Private pool diagnostics");
    }

    [IntegrationTheory]
    [InlineData("json", false, 0)]
    [InlineData("json", false, 1)]
    [InlineData("geojson", false, 0)]
    [InlineData("geojson", false, 1)]
    [InlineData("json", true, 0)]
    [InlineData("json", true, 1)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task StreamingQuery_EmptyOrSingleRow_PreservesRowsAndDisposesSource(string format, bool idsOnly, int count)
    {
        var starts = _state.StreamStarts;
        var disposals = _state.StreamDisposals;
        _state.StreamingFeatures = count == 0 ? [] : [Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty)];
        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=1001&f={format}&returnIdsOnly={idsOnly.ToString().ToLowerInvariant()}");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var document = JsonDocument.Parse(content);
            document.RootElement.GetProperty(idsOnly ? "objectIds" : "features").GetArrayLength().Should().Be(count);
            _state.StreamStarts.Should().Be(starts + 1);
            _state.StreamDisposals.Should().Be(disposals + 1);
        }
        finally
        {
            _state.StreamingFeatures = null;
        }
    }
}
