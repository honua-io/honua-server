// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Protocols.OData.ComponentTests;

/// <summary>The same HTTP contracts execute against the minimal host or a separately started full host.</summary>
[Trait("Category", "Integration")]
[Trait("Tier", "Integration")]
public sealed class ODataContractTests(ODataComponentFixture fixture) : IClassFixture<ODataComponentFixture>
{
    private const string Features = "/odata/Layers(0)/Features";

    [Fact]
    public async Task MetadataAndSeededQueries_ExposeRealCatalogAndSpatialPipeline()
    {
        using var metadata = await fixture.Client.GetAsync("/odata/$metadata");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        (await metadata.Content.ReadAsStringAsync()).Should().Contain("EntityType").And.Contain("Feature");
        using var service = await fixture.Client.GetAsync("/odata");
        service.StatusCode.Should().Be(HttpStatusCode.OK);
        using var query = await GetJsonAsync(Features + "?$filter=population%20gt%201000000&$orderby=population%20desc&$count=true");
        query.RootElement.GetProperty("@odata.count").GetInt64().Should().Be(4);
        query.RootElement.GetProperty("value").EnumerateArray().Select(x => Attributes(x).GetProperty("name").GetString())
            .Should().Equal("Los Angeles", "Phoenix");
        var spatial = "geo.intersects(Geometry, geography'POLYGON((-122.5 37.7,-122.3 37.7,-122.3 37.85,-122.5 37.85,-122.5 37.7))')";
        using var result = await GetJsonAsync(Features + "?$filter=" + Uri.EscapeDataString(spatial));
        result.RootElement.GetProperty("value").EnumerateArray().Should().Contain(x => Attributes(x).GetProperty("name").GetString() == "San Francisco");
    }

    [Fact]
    public async Task Paging_RespectsServerLimitAndPreservesDistinctRows()
    {
        string? next = Features + "?$orderby=ObjectId&$top=5";
        var ids = new List<long>();
        for (var page = 0; next is not null && page < 8; page++)
        {
            using var result = await GetJsonAsync(next);
            var values = result.RootElement.GetProperty("value").EnumerateArray().ToArray();
            values.Length.Should().BeLessThanOrEqualTo(2);
            ids.AddRange(values.Select(x => x.GetProperty("ObjectId").GetInt64()));
            next = result.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        next.Should().BeNull("paging must terminate");
        ids.Should().HaveCount(5).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task CrudAndEtags_UseCanonicalEditAndConcurrencyPipeline()
    {
        var name = "pilot-" + Guid.NewGuid().ToString("N");
        using var created = await fixture.Client.PostAsJsonAsync(Features, new { Attributes = new { name, population = 12345 }, Geometry = new { type = "Point", coordinates = new[] { -122.0, 37.0 } } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("ObjectId").GetInt64();
        var path = $"/odata/Features(LayerId=0,ObjectId={id})";
        try
        {
            using var read = await fixture.Client.GetAsync(path);
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var etag = read.Headers.ETag?.ToString();
            etag.Should().NotBeNullOrEmpty();
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var unchanged = await fixture.Client.SendAsync(conditional);
            unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
            using var patch = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new { Attributes = new { name, population = 54321 } }) };
            patch.Headers.TryAddWithoutValidation("If-Match", etag);
            using var updated = await fixture.Client.SendAsync(patch);
            updated.IsSuccessStatusCode.Should().BeTrue();
            using var stale = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new { Attributes = new { population = 1 } }) };
            stale.Headers.TryAddWithoutValidation("If-Match", etag);
            using var rejected = await fixture.Client.SendAsync(stale);
            rejected.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
            using var final = await GetJsonAsync(path);
            Attributes(final.RootElement).GetProperty("population").GetInt64().Should().Be(54321);
        }
        finally
        {
            using var deleted = await fixture.Client.DeleteAsync(path);
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using var missing = await fixture.Client.GetAsync(path);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AtomicBatchFailure_DoesNotPersistEarlierCreate()
    {
        var name = "rollback-" + Guid.NewGuid().ToString("N");
        var batch = new { requests = new object[] {
            new { id = "1", method = "POST", url = "Layers(0)/Features", atomicityGroup = "a", headers = new Dictionary<string,string> { ["Content-Type"] = "application/json" }, body = new { Attributes = new { name, population = 42 } } },
            new { id = "2", method = "PATCH", url = "Features(LayerId=0,ObjectId=9223372036854775806)", atomicityGroup = "a", headers = new Dictionary<string,string> { ["Content-Type"] = "application/json" }, body = new { Attributes = new { population = 1 } } }
        } };
        using var response = await fixture.Client.PostAsJsonAsync("/odata/$batch", batch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("responses").EnumerateArray().Should().Contain(x => x.GetProperty("status").GetInt32() >= 400);
        using var remaining = await GetJsonAsync(Features + "?$filter=" + Uri.EscapeDataString($"name eq '{name}'"));
        remaining.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PayloadLimits_RejectOversizedEditsBeforeMutation()
    {
        using var payload = JsonContent.Create(new { Attributes = new { name = new string('x', 8192) } });
        using var response = await fixture.Client.PostAsync(Features, payload);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task AuthenticationAndErrors_PreserveDenialAndProtocolEnvelope()
    {
        using var denied = await fixture.Anonymous.PostAsJsonAsync(Features, new { Attributes = new { name = "denied" } });
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var invalid = await fixture.Client.GetAsync(Features + "?$filter=population%20eq%20(");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var error = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        error.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        using var negative = await fixture.Client.GetAsync(Features + "?$top=-1");
        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Attributes(JsonElement feature)
    {
        var attributes = feature.GetProperty("Attributes");
        if (attributes.ValueKind != JsonValueKind.String) return attributes;
        using var document = JsonDocument.Parse(attributes.GetString()!);
        return document.RootElement.Clone();
    }
}
