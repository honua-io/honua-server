// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesCsvHeaderTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "id",
            Type = MetadataV2FieldType.String
        });
        await using var connection = await App.Postgres.GetConnectionAsync(App.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM features;
            INSERT INTO features (objectid, layer_id, attributes)
            SELECT i, 0, jsonb_build_object('id', 'property-' || i)
            FROM generate_series(1, 400) AS i;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesCsvHeaderTests(OgcFeaturesCsvHeaderTestsFixture fixture)
    : IClassFixture<OgcFeaturesCsvHeaderTestsFixture>
{
    [IntegrationTheory]
    [InlineData("items?limit=1&sortby=objectid", 1)]
    [InlineData("items?limit=400&sortby=objectid", 400)]
    [InlineData("items/1?", 1)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Csv_WithIdProperty_PreservesIdentifierAndProperty(string path, int count)
    {
        using var response = await fixture.App.Client.GetAsync(
            $"/ogc/features/collections/0/{path}&f=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StringReader(await response.Content.ReadAsStringAsync());
        var header = reader.ReadLine()!.Split(',');
        Assert.Equal("id_1", header[0]);
        Assert.Equal("geometry", header[^1]);
        Assert.Equal(header.Length, header.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var objectIdColumn = Array.IndexOf(header, "objectid");
        Assert.True(objectIdColumn > 0);
        var propertyColumn = Array.IndexOf(header, "id");
        Assert.True(propertyColumn > 0);
        for (var id = 1; id <= count; id++)
        {
            var row = reader.ReadLine()!.Split(',');
            Assert.Equal(header.Length, row.Length);
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), row[0]);
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), row[objectIdColumn]);
            Assert.Equal($"property-{id}", row[propertyColumn]);
            Assert.Equal(string.Empty, row[^1]);
        }

        Assert.Null(reader.ReadLine());
    }

    [IntegrationTheory]
    [InlineData("limit=2&offset=7&sortby=-objectid", 393, 392)]
    [InlineData("limit=2&filter=objectid%3E397&sortby=objectid", 398, 399)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task Csv_PagedOrFilteredRows_PreservesObjectIdAssociation(string query, int first, int second)
    {
        using var response = await fixture.App.Client.GetAsync(
            $"/ogc/features/collections/0/items?{query}&f=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StringReader(await response.Content.ReadAsStringAsync());
        var header = reader.ReadLine()!.Split(',');
        var objectIdColumn = Array.IndexOf(header, "objectid");
        var propertyColumn = Array.IndexOf(header, "id");
        Assert.True(objectIdColumn > 0);
        Assert.True(propertyColumn > 0);
        foreach (var id in new[] { first, second })
        {
            var row = reader.ReadLine()!.Split(',');
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), row[objectIdColumn]);
            Assert.Equal($"property-{id}", row[propertyColumn]);
        }
        Assert.Null(reader.ReadLine());
    }

    [IntegrationTheory]
    [InlineData("id", false)]
    [InlineData("id,objectid", true)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task Csv_ProjectedProperties_OnlyIncludesRequestedObjectId(string properties, bool includesObjectId)
    {
        using var response = await fixture.App.Client.GetAsync(
            $"/ogc/features/collections/0/items?ids=1&properties={properties}&f=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StringReader(await response.Content.ReadAsStringAsync());
        var header = reader.ReadLine()!.Split(',');
        Assert.Equal(includesObjectId, header.Contains("objectid"));
        var row = reader.ReadLine()!.Split(',');
        Assert.Equal("1", row[0]);
        Assert.Equal("property-1", row[Array.IndexOf(header, "id")]);
        if (includesObjectId)
        {
            Assert.Equal("1", row[Array.IndexOf(header, "objectid")]);
        }
        Assert.Null(reader.ReadLine());
    }
}
