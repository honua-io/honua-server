// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesHiddenFieldTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "category", Type = MetadataV2FieldType.String, Hidden = true
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("items?f=gml&limit=1")]
    [InlineData("items/1?f=gml")]
    [InlineData("items?f=csv&limit=1")]
    [InlineData("items/1?f=csv")]
    [InlineData("items?limit=1")]
    [InlineData("items/1")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Output_OmitsHiddenFields(string suffix)
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/0/{suffix}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("Test Feature");
        body.Should().NotContain("category");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task Queryables_OmitsHiddenFields()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/queryables");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.GetProperty("properties");
        properties.TryGetProperty("name", out _).Should().BeTrue();
        properties.TryGetProperty("category", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("properties=category")]
    [InlineData("sortby=category")]
    [InlineData("category=test")]
    [InlineData("filter=category%20%3D%20%27test%27")]
    [InlineData("filter-lang=cql2-json&filter=%7B%22op%22%3A%22%3D%22%2C%22args%22%3A%5B%7B%22property%22%3A%22category%22%7D%2C%22test%22%5D%7D")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task Query_RejectsHiddenFields(string query)
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/0/items?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GmlStream_OmitsHiddenFields()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (objectid, layer_id, attributes)
            SELECT i, 0, jsonb_build_object('name', 'Visible stream row', 'category', 'hidden-stream-value')
            FROM generate_series(1001, 1300) AS i;
            """;
        await command.ExecuteNonQueryAsync();
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items?f=gml&limit=300");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var document = XDocument.Parse(body);
        document.Root!.Attribute("numberReturned")!.Value.Should().Be("300");
        document.Descendants().Count(e => e.Name.LocalName == "member").Should().Be(300);
        body.Should().Contain("Visible stream row");
        body.Should().NotContain("category").And.NotContain("hidden-stream-value");
    }
}
