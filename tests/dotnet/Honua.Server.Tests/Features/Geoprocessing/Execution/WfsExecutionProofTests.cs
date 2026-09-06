// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Sdk;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>Real paged WFS execution with independent feature and ordinate assertions.</summary>
[Trait("Category", "RemoteSourceExecutionProof")]
public sealed class WfsExecutionProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wfs_FilteredShortPages_PublishesExactFeaturesAndTerminates(bool numberMatched)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var server = builder.Build();
        var starts = new List<int>();
        // Fixture rows excluded independently by type, predicate, and spatial window.
        (int Id, string Type, bool Active, double X, double Y, double Z, string? Name)[] rows =
        [
            (11, "survey:points", true, 1, 2, 3.25, "Kīlauea 日本"),
            (12, "survey:points", true, 3, 4, -1.5, null),
            (13, "survey:points", true, 5, 6, 9.75, "last"),
            (21, "other:points", true, 1, 2, 3, "wrong type"),
            (22, "survey:points", false, 1, 2, 3, "inactive"),
            (23, "survey:points", true, 50, 60, 3, "outside")
        ];
        server.MapGet("/wfs", (HttpRequest request) =>
        {
            var q = request.Query;
            q["service"].ToString().Should().Be("WFS");
            q["request"].ToString().Should().Be("GetFeature");
            q["version"].ToString().Should().Be("2.0.0");
            q["outputFormat"].ToString().Should().Be("application/json");
            q["count"].ToString().Should().Be("2");
            var start = int.Parse(q["startIndex"].ToString(), CultureInfo.InvariantCulture);
            starts.Add(start);
            starts.Count.Should().BeLessThanOrEqualTo(4, "paging must terminate");
            var selected = rows.Where(r => r.Type == q["typeNames"].ToString());
            if (q["CQL_FILTER"].ToString() == "active = true")
            {
                selected = selected.Where(r => r.Active);
            }
            if (q["bbox"].ToString() == "0,0,10,10")
            {
                selected = selected.Where(r => r.X >= 0 && r.X <= 10 && r.Y >= 0 && r.Y <= 10);
            }
            var filtered = selected.ToArray();
            var features = filtered.Skip(start).Take(1).Select(r => new
            {
                type = "Feature",
                id = r.Id,
                geometry = new { type = "Point", coordinates = new[] { r.X, r.Y, r.Z } },
                properties = new { key = r.Id, serial = 9007199254740993L + r.Id, name = r.Name, active = r.Active }
            });
            // Deliberately cap below requested count to catch premature termination.
            return Results.Json(new { type = "FeatureCollection", numberMatched = numberMatched ? (int?)filtered.Length : null, features });
        });
        await server.StartAsync();
        using var client = new HttpClient(new FixtureTransport(new Uri(server.Urls.Single())));
        var services = new ServiceCollection();
        services.AddSingleton<IDagFeatureSource>(new WfsDagSource(client, NullLogger<WfsDagSource>.Instance));
        using var provider = services.BuildServiceProvider();
        using var output = await RemoteSourceProof.Execute(provider, "source.wfs",
            ("serviceUrl", "https://8.8.8.8/wfs"), ("typeName", "survey:points"),
            ("where", "active = true"), ("bbox", "0,0,10,10"), ("pageSize", "2"));
        AssertFeatures(output.RootElement);
        var wrong = JsonNode.Parse(output.RootElement.GetRawText())!;
        // A duplicate page is valid GeoJSON but violates completeness and uniqueness.
        wrong["features"]![2] = wrong["features"]![1]!.DeepClone();
        using var duplicatePage = JsonDocument.Parse(wrong.ToJsonString());
        Action rejectDuplicate = () => AssertFeatures(duplicatePage.RootElement);
        rejectDuplicate.Should().Throw<XunitException>();
        starts.Should().Equal(numberMatched ? [0, 1, 2] : new[] { 0, 1, 2, 3 });
    }

    private static void AssertFeatures(JsonElement root)
    {
        var features = root.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(3);
        features.Select(f => f.GetProperty("properties").GetProperty("key").GetInt32()).Should().Equal(11, 12, 13);
        double[][] coordinates = [[1, 2, 3.25], [3, 4, -1.5], [5, 6, 9.75]];
        string?[] names = ["Kīlauea 日本", null, "last"];
        for (var i = 0; i < 3; i++)
        {
            features[i].GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
            features[i].GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(v => v.GetDouble())
                .Should().Equal(coordinates[i]);
            features[i].GetProperty("properties").GetProperty("name").GetString().Should().Be(names[i]);
            features[i].GetProperty("properties").GetProperty("active").GetBoolean().Should().BeTrue();
            features[i].GetProperty("properties").GetProperty("serial").GetInt64().Should().Be(9007199254741004L + i);
        }
    }

    // Remap only the HTTP transport to the real local fixture server. The production
    // executor's SSRF validation still runs against a public numeric address, with no
    // live DNS or remote WFS dependency and no private-network opt-out in production.
    private sealed class FixtureTransport(Uri server) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(server, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
