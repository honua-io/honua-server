// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using Honua.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesStreamingTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .UseKestrel()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureServices(services => services.Configure<LimitsOptions>(options =>
            options.Query.DefaultRecordCount = 300));

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.Client.Timeout = TimeSpan.FromSeconds(15);
        await using var connection = await App.Postgres.GetConnectionAsync(App.CurrentSchema!);
        await using var command = connection.CreateCommand();
        // A private schema with known ids, attributes and ordinates, independent of serializer output.
        command.CommandText = """
            DELETE FROM features;
            INSERT INTO features (objectid, layer_id, geometry, attributes)
            SELECT i, 0, CASE WHEN i % 10 = 0 THEN NULL
                ELSE ST_SetSRID(ST_MakePoint(-120 + i * 0.001, 30 + i * 0.001), 4326) END,
                jsonb_build_object('name', repeat('x', 1024), 'population', i, 'category', 'stream-fixture')
            FROM generate_series(1, 1200) AS i;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
[Collection("Database")]
public sealed class OgcFeaturesStreamingTests : IClassFixture<OgcFeaturesStreamingTestsFixture>
{
    private readonly WebAppFixture _fixture;
    private const int TestLayerId = 0;

    public OgcFeaturesStreamingTests(OgcFeaturesStreamingTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_LargeLimit_UsesStreamingResponse()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false,
            "Expected chunked transfer encoding for streaming responses");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        document.RootElement.GetProperty("features").EnumerateArray().Should().NotBeEmpty();
        document.RootElement.TryGetProperty("numberMatched", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("numberReturned", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items?f=gml")]
    public async Task GetItems_LargeLimit_Gml_UsesStreamingResponse()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2000&f=gml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false,
            "Expected chunked transfer encoding for streaming GML responses");

        response.Content.Headers.ContentType?.MediaType.Should().Contain("application/gml+xml");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("<wfs:FeatureCollection");
    }

    [IntegrationTheory]
    [InlineData(null, false)]
    [InlineData(100, false)]
    [InlineData(400, false)]
    [InlineData(1000, false)]
    [InlineData(null, true)]
    [InlineData(100, true)]
    [InlineData(400, true)]
    [InlineData(1000, true)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_KeepAlive_CompletesPagesAndReusesConnection(int? limit, bool gml)
    {
        var connections = 0;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                Interlocked.Increment(ref connections);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = _fixture.Client.BaseAddress,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(15)
        };
        foreach (var header in _fixture.Client.DefaultRequestHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        var expectedCount = limit ?? 300;
        var query = gml ? "sortby=objectid&f=gml" : "sortby=objectid";
        if (limit.HasValue)
        {
            query += $"&limit={limit.Value}";
        }

        // A second successful response on the same socket proves that the first response's
        // framing terminated. Reading valid JSON alone would miss the original defect.
        for (var page = 0; page < 2; page++)
        {
            using var response = await client.GetAsync(
                $"/ogc/features/collections/{TestLayerId}/items?{query}&offset={page * expectedCount}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Version.Should().Be(HttpVersion.Version11);
            response.Headers.ConnectionClose.Should().NotBe(true);
            if (expectedCount > 200)
            {
                response.Headers.TransferEncodingChunked.Should().BeTrue();
            }
            response.Headers.GetValues("Content-Crs").Should()
                .ContainSingle().Which.Should().Contain("CRS84");
            var body = await response.Content.ReadAsByteArrayAsync();
            var returned = Math.Min(expectedCount, 1200 - page * expectedCount);
            if (returned >= 300)
            {
                body.Length.Should().BeGreaterThan(256 * 1024);
            }

            if (gml)
            {
                var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(body));
                XNamespace wfs = "http://www.opengis.net/wfs/2.0";
                XNamespace gmlNamespace = "http://www.opengis.net/gml/3.2";
                document.Root!.Attribute("numberMatched")!.Value.Should().Be("1200");
                document.Root.Attribute("numberReturned")!.Value.Should().Be(returned.ToString(CultureInfo.InvariantCulture));
                DateTimeOffset.TryParse(document.Root.Attribute("timeStamp")!.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _).Should().BeTrue();
                response.Headers.GetValues("Link").Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal))
                    .Should().Be((page + 1) * expectedCount < 1200);
                var members = document.Root.Elements(wfs + "member").ToArray();
                members.Should().HaveCount(returned);
                for (var i = 0; i < returned; i++)
                {
                    var id = page * expectedCount + i + 1;
                    members[i].Elements().Single().Attribute(gmlNamespace + "id")!.Value
                        .Should().Be($"feature_{id}");
                    var position = members[i].Descendants(gmlNamespace + "pos").SingleOrDefault();
                    if (id % 10 == 0)
                    {
                        position.Should().BeNull();
                    }
                    else
                    {
                        var ordinates = position!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                        ordinates.Should().HaveCount(2);
                        ordinates[0].Should().BeApproximately(-120 + id * 0.001, 1e-9);
                        ordinates[1].Should().BeApproximately(30 + id * 0.001, 1e-9);
                    }
                    members[i].Descendants().Single(element => (string?)element.Attribute("name") == "name")
                        .Value.Should().Be(new string('x', 1024));
                    members[i].Descendants().Single(element => (string?)element.Attribute("name") == "population")
                        .Value.Should().Be(id.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                root.GetProperty("numberMatched").GetInt32().Should().Be(1200);
                root.GetProperty("numberReturned").GetInt32().Should().Be(returned);
                root.GetProperty("type").GetString().Should().Be("FeatureCollection");
                DateTimeOffset.TryParse(root.GetProperty("timeStamp").GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _).Should().BeTrue();
                var features = root.GetProperty("features").EnumerateArray().ToArray();
                features.Should().HaveCount(returned);
                for (var i = 0; i < returned; i++)
                {
                    var id = page * expectedCount + i + 1;
                    features[i].GetProperty("id").GetInt64().Should().Be(id);
                    var properties = features[i].GetProperty("properties");
                    properties.GetProperty("name").GetString().Should().Be(new string('x', 1024));
                    properties.GetProperty("population").GetInt32().Should().Be(id);
                    var geometry = features[i].GetProperty("geometry");
                    if (id % 10 == 0)
                    {
                        geometry.ValueKind.Should().Be(JsonValueKind.Null);
                    }
                    else
                    {
                        geometry.GetProperty("type").GetString().Should().Be("Point");
                        var ordinates = geometry.GetProperty("coordinates");
                        ordinates.GetArrayLength().Should().Be(2);
                        ordinates[0].GetDouble().Should().BeApproximately(-120 + id * 0.001, 1e-9);
                        ordinates[1].GetDouble().Should().BeApproximately(30 + id * 0.001, 1e-9);
                    }
                }
                root.GetProperty("links").EnumerateArray().Any(link => link.GetProperty("rel").GetString() == "next")
                    .Should().Be((page + 1) * expectedCount < 1200);
            }
        }
        connections.Should().Be(1, "both complete pages must reuse the same keep-alive connection");
    }

    /// <summary>
    /// Regression for BH4-008: streaming responses must expose an advisory
    /// <c>numberMatched</c> snapshot count in the JSON <c>FeatureCollection</c>
    /// body — the spec-compliant location (OGC 17-069r4 §7.14.4). The value is a
    /// pre-flight snapshot estimate — advisory per OGC API Features Part 1 §7.7,
    /// not an authoritative exact count — but it must always be a non-negative
    /// integer, never absent or negative.
    ///
    /// The non-standard <c>OGC-NumberMatched</c> response header was removed in
    /// #2418 (PA-122/PA-168); this test now guards that it stays absent so the
    /// count is carried only in its spec-compliant body location.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_LargeLimit_Streaming_ProvidesAdvisorySnapshotNumberMatched()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false,
            "Response must use chunked encoding (streaming path)");

        // JSON body must contain numberMatched as an advisory snapshot count.
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("numberMatched", out var nmProp)
            .Should().BeTrue("streaming path must include an advisory numberMatched count (BH4-008)");
        nmProp.GetInt64().Should().BeGreaterThanOrEqualTo(0,
            "numberMatched snapshot estimate must be a non-negative integer");

        // The non-standard OGC-NumberMatched header was removed in #2418 (PA-122/PA-168):
        // the count lives only in the spec-compliant JSON body location above. Guard that the
        // header is not reintroduced.
        response.Headers.TryGetValues("OGC-NumberMatched", out _)
            .Should().BeFalse("the non-standard OGC-NumberMatched header was removed in #2418 (PA-122/PA-168)");
    }
}
