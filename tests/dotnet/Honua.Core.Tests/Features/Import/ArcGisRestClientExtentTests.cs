// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisRestClientExtentTests
{
    [Fact]
    public async Task QueryFeatureExtentAsync_RequestsTheFeatureExtentAndIgnoresAdvertisedMetadata()
    {
        using var handler = new ExtentSourceHandler();
        using var http = new HttpClient(handler);
        var client = new ArcGisRestClient(http, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var extent = await client.QueryFeatureExtentAsync(
            "https://example.com/arcgis/rest/services/Tracks/FeatureServer",
            0,
            "DateOfFlight = DATE '2026-01-11'",
            5,
            0,
            CancellationToken.None);

        handler.Query.Should().Contain("returnExtentOnly=true");
        handler.Query.Should().Contain("where=");
        extent.HasValue.Should().BeTrue();
        var box = extent.GetValueOrDefault();
        box.MinX.Should().Be(-158);
        box.MinY.Should().Be(21);
        box.MaxX.Should().Be(-157);
        box.MaxY.Should().Be(22);
        box.SpatialReferenceId.Should().Be(4326);
    }

    private sealed class ExtentSourceHandler : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri!.Query;
            const string json = """
                {"extent":{"xmin":-158,"ymin":21,"xmax":-157,"ymax":22,"spatialReference":{"wkid":4326}}}
                """;
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
