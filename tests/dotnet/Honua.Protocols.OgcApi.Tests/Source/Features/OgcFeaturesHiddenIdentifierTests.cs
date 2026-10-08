// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Provides a PostGIS application whose custom public identifier is hidden as an attribute.
/// </summary>
public sealed class OgcFeaturesHiddenIdentifierFixture : IAsyncLifetime
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
            SemanticRoles = ["id.primary"],
            Hidden = true
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

/// <summary>
/// Verifies hiding an identifier attribute preserves collection and item identity binding.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesHiddenIdentifierTests : IClassFixture<OgcFeaturesHiddenIdentifierFixture>
{
    private readonly WebAppFixture _fixture;

    public OgcFeaturesHiddenIdentifierTests(OgcFeaturesHiddenIdentifierFixture fixture)
    {
        _fixture = fixture.App;
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task HiddenCustomIdentifier_CollectionIdsAndSingleLookupAgree()
    {
        var collectionResponse = await _fixture.Client.GetAsync("/ogc/features/collections/0/items?ids=public-1");
        var singleResponse = await _fixture.Client.GetAsync("/ogc/features/collections/0/items/public-1");
        var collectionBody = await collectionResponse.Content.ReadAsStringAsync();
        var singleBody = await singleResponse.Content.ReadAsStringAsync();
        collectionResponse.StatusCode.Should().Be(HttpStatusCode.OK, collectionBody);
        singleResponse.StatusCode.Should().Be(HttpStatusCode.OK, singleBody);
        using var single = JsonDocument.Parse(singleBody);
        single.RootElement.GetProperty("id").GetString().Should().Be("public-1");
        single.RootElement.GetProperty("properties").TryGetProperty("public_id", out _).Should().BeFalse();
        using var collection = JsonDocument.Parse(collectionBody);
        var features = collection.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().ContainSingle();
        features[0].GetProperty("id").GetString().Should().Be("public-1");
        features[0].GetProperty("properties").TryGetProperty("public_id", out _).Should().BeFalse();
    }
}
