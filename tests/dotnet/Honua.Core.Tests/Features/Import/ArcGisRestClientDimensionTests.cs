// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisRestClientDimensionTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public async Task QueryFeaturesAsync_PreservesSourceOrdinatesInBothPagingModes(bool objectIdWindow, int dimensions)
    {
        using var handler = new DimensionalSourceHandler(dimensions);
        using var http = new HttpClient(handler);
        var client = new ArcGisRestClient(http, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var result = await client.QueryFeaturesAsync(
            "https://example.com/arcgis/rest/services/Tracks/FeatureServer", 0, 0, 100,
            null, null, 4326, 5, 0, CancellationToken.None,
            objectIds: objectIdWindow ? [42] : null);

        handler.Query.Should().Contain("returnZ=true").And.Contain("returnM=true");
        handler.Query.Should().Contain(objectIdWindow ? "objectIds=42" : "resultOffset=0");
        var coordinate = result.Features.Single().Geometry!.Value.GetProperty("paths")[0][0];
        coordinate.GetArrayLength().Should().Be(dimensions);
        if (dimensions >= 3)
        {
            coordinate[2].GetDouble().Should().Be(123.5);
        }
        if (dimensions == 4)
        {
            coordinate[3].GetDouble().Should().Be(456.5);
        }
    }

    private sealed class DimensionalSourceHandler(int dimensions) : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri!.Query;
            // Model the observed source boundary: absent flags return only X/Y.
            var requested = Query.Contains("returnZ=true", StringComparison.Ordinal) &&
                Query.Contains("returnM=true", StringComparison.Ordinal);
            var coordinate = !requested || dimensions == 2 ? "[1,2]" :
                dimensions == 3 ? "[1,2,123.5]" : "[1,2,123.5,456.5]";
            var json = "{\"features\":[{\"attributes\":{\"OBJECTID\":42},\"geometry\":{\"paths\":[[" +
                coordinate + "," + coordinate + "]]}}]}";
            // HttpClient owns and disposes the returned response.
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
