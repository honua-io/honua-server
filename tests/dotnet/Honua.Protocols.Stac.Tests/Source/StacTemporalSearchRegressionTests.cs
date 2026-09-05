// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacTemporalSearchRegressionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resourceId = snapshot.Graph.Publications.First(p => p.LayerIndex == WebAppFixture.TestLayerId).ResourceId;
        foreach (var field in snapshot.Graph.Resources.First(r => r.Metadata.Id == resourceId).SchemaFields
                     .Where(f => f.Type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time))
        {
            _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, field with { Type = MetadataV2FieldType.String });
        }
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("items")]
    [InlineData("get")]
    [InlineData("post")]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    [Endpoint("GET /stac/search")]
    [Endpoint("POST /stac/search")]
    public async Task Datetime_WithoutTemporalField_DoesNotReturnUndatedRows(string route)
    {
        const string datetime = "1999-01-01T00:00:00Z/1999-12-31T23:59:59Z";
        var collectionId = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = route switch
        {
            "items" => await _fixture.Client.GetAsync($"/stac/collections/{collectionId}/items?datetime={datetime}"),
            "get" => await _fixture.Client.GetAsync($"/stac/search?collections={collectionId}&datetime={datetime}"),
            _ => await _fixture.Client.PostAsJsonAsync("/stac/search", new { collections = new[] { collectionId }, datetime })
        };

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("features").EnumerateArray().Should().BeEmpty();
        json.RootElement.GetProperty("numberMatched").GetInt64().Should().Be(0);
        json.RootElement.GetProperty("links").EnumerateArray()
            .Should().NotContain(link => link.GetProperty("rel").GetString() == "next");
    }
}
