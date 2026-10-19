// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Web;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisRestClientExtentTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("\"wkid\":4326", 4326)]
    [InlineData("\"wkid\":102100,\"latestWkid\":3857", 3857)]
    public async Task QueryFeatureExtentAsync_RequestsTheFeatureExtentAndIgnoresAdvertisedMetadata(
        string spatialReference, int expectedSrid)
    {
        using var handler = new ExtentSourceHandler(spatialReference);
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

        var parameters = HttpUtility.ParseQueryString(handler.Query);
        parameters.GetValues("returnExtentOnly").Should().Equal("true");
        parameters["where"].Should().Be("DateOfFlight = DATE '2026-01-11'");
        extent.HasValue.Should().BeTrue();
        var box = extent.GetValueOrDefault();
        box.MinX.Should().Be(-158);
        box.MinY.Should().Be(21);
        box.MaxX.Should().Be(-157);
        box.MaxY.Should().Be(22);
        box.SpatialReferenceId.Should().Be(expectedSrid);
    }

    private sealed class ExtentSourceHandler(string spatialReference) : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri!.Query;
            // The live source rejects repeated scalar flags, even when their values agree.
            // A permissive fixture used to hide the failed source-extent observation.
            if (HttpUtility.ParseQueryString(Query).GetValues("returnExtentOnly") is not ["true"])
            {
                return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"code\":400,\"message\":\"Invalid returnExtentOnly parameter\"}}",
                        Encoding.UTF8, "application/json")
                });
            }

            var json = $$"""
                {"extent":{"xmin":-158,"ymin":21,"xmax":-157,"ymax":22,"spatialReference":{ {{spatialReference}} } } }
                """;
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
