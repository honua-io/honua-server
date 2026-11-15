// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesProjectedIdentifierFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "objectid",
            Type = MetadataV2FieldType.BigInteger
        });
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "public_id",
            Type = MetadataV2FieldType.String,
            SemanticRoles = ["id.primary"]
        });
        await using var connection = await App.Postgres.GetConnectionAsync(App.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features SET attributes = attributes || jsonb_build_object('public_id', 'public-' || objectid)
            WHERE layer_id = 0;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesProjectedIdentifierTests : IClassFixture<OgcFeaturesProjectedIdentifierFixture>
{
    private readonly WebAppFixture _fixture;

    public OgcFeaturesProjectedIdentifierTests(OgcFeaturesProjectedIdentifierFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTheory]
    [InlineData("name", false)]
    [InlineData("name,public_id", true)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_ProjectedProperties_PreservesIdentityWithoutAddingUnrequestedIdentifier(
        string properties, bool includeIdentifier)
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/0/items?ids=public-1&properties={properties}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().ContainSingle();
        var feature = features[0];
        feature.GetProperty("id").GetString().Should().Be("public-1");
        var names = feature.GetProperty("properties").EnumerateObject().Select(property => property.Name);
        names.Should().BeEquivalentTo(includeIdentifier ? new[] { "name", "public_id" } : new[] { "name" });
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_GmlProjectedProperties_DoesNotAddIdentifierProperty()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/features/collections/0/items?ids=public-1&properties=name&f=gml");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var document = XDocument.Parse(body);
        document.Descendants().Where(element => element.Name.LocalName == "name").Should().ContainSingle();
        document.Descendants().Where(element => element.Name.LocalName == "public_id").Should().BeEmpty();
    }

}
