// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Geoprocessing;

public sealed class OgcFeaturesDagSourceOriginTests
{
    private const string ServiceUrl = "https://maps.example.com/ogc";

    [UnitTest]
    public async Task NextLinkOnTheServiceOrigin_IsFollowedWithCredentials()
    {
        using var handler = new RecordingHandler(PageLinkingTo("/ogc/collections/parcels/items?offset=1"), EmptyPage);

        await ReadAllAsync(handler);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Uri.Should().Be("https://maps.example.com/ogc/collections/parcels/items?offset=1");
        handler.Requests[1].AuthorizationScheme.Should().Be("Basic");
    }

    [UnitTest]
    public async Task NextLinkOnAnotherHost_IsRefusedWithoutARequest()
    {
        using var handler = new RecordingHandler(PageLinkingTo("https://elsewhere.example.net/page2"));

        var read = () => ReadAllAsync(handler);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different origin*");
        handler.Requests.Should().ContainSingle();
    }

    [UnitTest]
    public async Task NextLinkThatDowngradesToHttp_IsRefusedWithoutARequest()
    {
        using var handler = new RecordingHandler(PageLinkingTo("http://maps.example.com/ogc/page2"));

        var read = () => ReadAllAsync(handler);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different origin*");
        handler.Requests.Should().ContainSingle();
    }

    [UnitTest]
    public async Task NextLinkOnAnotherPort_IsRefusedWithoutARequest()
    {
        using var handler = new RecordingHandler(PageLinkingTo("https://maps.example.com:8443/ogc/page2"));

        var read = () => ReadAllAsync(handler);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different origin*");
        handler.Requests.Should().ContainSingle();
    }

    private const string EmptyPage = """{"type":"FeatureCollection","features":[]}""";

    private static string PageLinkingTo(string href) => $$$"""
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"id":1}}
        ],"links":[{"rel":"next","href":"{{{href}}}"}]}
        """;

    private static async Task ReadAllAsync(RecordingHandler handler)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        var reader = new OgcFeaturesDagSource(client, NullLogger<OgcFeaturesDagSource>.Instance);
        var request = new DagSourceRequest
        {
            ServiceUrl = ServiceUrl,
            CollectionId = "parcels",
            Username = "reader",
            Password = "secret",
        };

        var features = new List<DagSourceFeature>();
        await foreach (var feature in reader.ReadAsync(request))
        {
            features.Add(feature);
        }
    }

    private sealed record RecordedRequest(string Uri, string? AuthorizationScheme);

    private sealed class RecordingHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.Scheme));
            var body = _bodies.Count > 0 ? _bodies.Dequeue() : EmptyPage;
            return Task.FromResult<HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
