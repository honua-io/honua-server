// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesWithheldFieldSchemaTestsFixture : IAsyncLifetime
{
    public const string MaskedField = "notes";

    public WebAppFixture App { get; } = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IFieldMaskSource>(new NotesFieldMaskSource());

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        await using var connection = await App.Postgres.GetConnectionAsync(App.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM features;
            INSERT INTO features (objectid, layer_id, attributes)
            SELECT i, 0, jsonb_build_object('name', 'n-' || i, 'notes', 'withheld-' || i)
            FROM generate_series(1, 400) AS i;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();

    private sealed class NotesFieldMaskSource : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create(MaskedField));
    }
}

// The caller's field mask withholds "notes": no OGC Features schema listing may name it.
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesWithheldFieldSchemaTests(OgcFeaturesWithheldFieldSchemaTestsFixture fixture)
    : IClassFixture<OgcFeaturesWithheldFieldSchemaTestsFixture>
{
    private const string MaskedField = OgcFeaturesWithheldFieldSchemaTestsFixture.MaskedField;

    [IntegrationTheory]
    [InlineData("items?limit=1&sortby=objectid")]
    [InlineData("items?limit=400&sortby=objectid")]
    [InlineData("items/1?")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Csv_HeaderOmitsMaskedField(string path)
    {
        using var response = await fixture.App.Client.GetAsync($"/ogc/features/collections/0/{path}&f=csv");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = body.Split('\n')[0].TrimEnd('\r').Split(',');
        Assert.Contains("name", header);
        Assert.DoesNotContain(MaskedField, header, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("withheld-", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task Queryables_OmitMaskedField()
    {
        using var response = await fixture.App.Client.GetAsync("/ogc/features/collections/0/queryables?f=json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var properties = document.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("name", properties);
        Assert.DoesNotContain(MaskedField, properties, StringComparer.OrdinalIgnoreCase);
    }
}
