// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisRestClientCountTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("DateOfFlight = DATE '2025-01-11' AND OBJECTID IN (42, 99)")]
    [InlineData("Name = 'A&B' AND Name <> 'x+y'")]
    public async Task GetLayerInfoAsync_CountsSelectedSourcePopulationWithoutRewritingFilter(string? where)
    {
        using var handler = new CountSourceHandler();
        using var http = new HttpClient(handler);
        var client = new ArcGisRestClient(http, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var result = await client.GetLayerInfoAsync(
            "https://example.com/arcgis/rest/services/Tracks/FeatureServer", 0, 5, 0,
            CancellationToken.None, countWhereClause: where);

        handler.CountWhere.Should().Be(string.IsNullOrWhiteSpace(where) ? "1=1" : where);
        result.FeatureCount.Should().Be(string.IsNullOrWhiteSpace(where) ? 100 : 2);
    }

    private sealed class CountSourceHandler : HttpMessageHandler
    {
        public string? CountWhere { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = "{\"id\":0,\"name\":\"Tracks\",\"fields\":[]}";
            if (request.RequestUri!.AbsolutePath.EndsWith("/query", StringComparison.Ordinal))
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&')
                    .Select(part => part.Split('=', 2))
                    .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
                query["returnCountOnly"].Should().Be("true");
                CountWhere = query["where"];
                json = CountWhere == "1=1" ? "{\"count\":100}" : "{\"count\":2}";
            }

            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
