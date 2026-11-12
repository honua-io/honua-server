// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http.Features;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class FeatureServerQueryExecutorTests
{
    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData("json")]
    [InlineData("geojson")]
    public async Task StreamQueryAsync_FailureAfterResponseStarted_AbortsTheConnection(string format)
    {
        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(FeaturesThenFailure(failAfter: 200));
        var sut = CreateSut(Substitute.For<IFeatureReader>(), streamingStore);
        var context = CreateHttpContext();
        var body = (MemoryStream)context.Response.Body;
        context.Features.Set<IHttpResponseFeature>(new StartedOnFirstWriteResponseFeature(body));
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var service = CreateService();
        var resource = CreateResource();

        var act = () => sut.StreamQueryAsync(
            service,
            resource,
            CreatePublication(service, resource),
            7,
            new FeatureQuery { Limit = 2_000 },
            new QueryParameters { F = format, ReturnGeometry = false },
            outputSrid: null,
            context: context,
            cancellationToken: CancellationToken.None);

        // A started 200 that ends cleanly would hand the client a truncated page as if it were complete.
        await act.Should().ThrowAsync<TimeoutException>();
        body.Length.Should().BePositive();
        lifetime.Aborted.Should().BeTrue();
    }

    private static async IAsyncEnumerable<Feature> FeaturesThenFailure(int failAfter)
    {
        await Task.Yield();
        for (var id = 1L; id <= failAfter; id++)
        {
            yield return CreateFeature(id, "feature");
        }

        throw new TimeoutException("statement timeout");
    }

    private sealed class StartedOnFirstWriteResponseFeature(MemoryStream body) : HttpResponseFeature
    {
        public override bool HasStarted => body.Length > 0;
    }

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }
}
