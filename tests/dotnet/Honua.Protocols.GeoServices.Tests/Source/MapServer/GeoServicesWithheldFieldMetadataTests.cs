// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

// The caller's mask withholds "notes" and the publisher hides "description"; no metadata surface may name either.
[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class GeoServicesWithheldFieldMetadataTests(GeoServicesWithheldFieldMetadataFixture fixture)
    : IClassFixture<GeoServicesWithheldFieldMetadataFixture>
{
    private readonly WebAppFixture _app = fixture.App;

    [IntegrationTheory]
    [InlineData("/rest/services/test/MapServer/0?f=json")]
    [InlineData("/rest/services/test/MapServer/layers?f=json")]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/layers")]
    public async Task MapServerLayerMetadata_ListsNeitherHiddenNorMaskedFields(string path)
    {
        var body = await GetOkAsync(path);
        using var document = JsonDocument.Parse(body);
        var layer = document.RootElement.TryGetProperty("layers", out var layers) ? layers[0] : document.RootElement;

        var names = layer.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString());

        names.Should().Contain("name").And.NotContain("description").And.NotContain("notes");
        body.Should().NotContain("notes-domain");
    }

    [IntegrationTheory]
    [InlineData("/rest/services/test/FeatureServer/queryDomains?f=json")]
    [InlineData("/rest/services/test/MapServer/queryDomains?f=json")]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryDomains")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/queryDomains")]
    public async Task QueryDomains_OmitsMaskedFieldDomains(string path)
    {
        var body = await GetOkAsync(path);

        body.Should().Contain("category-domain", "a visible field's domain is still listed");
        body.Should().NotContain("notes-domain").And.NotContain("Confidential");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServerLayerMetadata_DropsRendererPopupAndEditorFieldNamingMaskedField()
    {
        using var document = JsonDocument.Parse(await GetOkAsync("/rest/services/test/FeatureServer/0?f=json"));
        var layer = document.RootElement;

        layer.GetProperty("drawingInfo").GetProperty("renderer").GetProperty("type").GetString()
            .Should().Be("simple", "a renderer keyed on a withheld field falls back to the default symbol");
        layer.TryGetProperty("popupInfo", out var popup).Should().BeFalse(popup.ToString());
        var editFields = layer.GetProperty("editFieldsInfo");
        editFields.TryGetProperty("creatorField", out var creator).Should().BeFalse(creator.ToString());
        editFields.GetProperty("editorField").GetString().Should().Be("name");
        layer.GetRawText().Should().NotContain("notes").And.NotContain("Confidential");
    }

    private async Task<string> GetOkAsync(string path)
    {
        using var response = await _app.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }
}

public sealed class GeoServicesWithheldFieldMetadataFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .ReplaceService<IFieldMaskSource>(new NotesFieldMaskSource());

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        await App.UpdateV2ResourceAsync(WebAppFixture.TestLayerId, resource => resource with
        {
            SchemaFields = [.. resource.SchemaFields.Select(WithFixtureVisibility)],
            Editing = (resource.Editing ?? new MetadataV2ObjectEditing()) with { CreatorField = "notes", EditorField = "name" },
            Extensions = new Dictionary<string, JsonElement>(resource.Extensions)
            {
                ["drawingInfo"] = JsonElement.Parse("""
                    {"renderer":{"type":"uniqueValue","field1":"notes",
                     "uniqueValueInfos":[{"value":"Confidential","label":"Confidential"}]}}
                    """),
                ["popupInfo"] = JsonElement.Parse("""{"title":"{notes}"}"""),
            },
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();

    private static MetadataV2Field WithFixtureVisibility(MetadataV2Field field) => field.Name switch
    {
        "description" => field with { Hidden = true },
        "notes" => field with { Domain = CodedDomain("notes-domain", "Confidential") },
        "category" => field with { Domain = CodedDomain("category-domain", "Park") },
        _ => field,
    };

    private static MetadataV2FieldDomain CodedDomain(string name, string code) => new()
    {
        Name = name,
        Type = "codedValue",
        CodedValues = [new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement(code), Name = code }],
    };

    private sealed class NotesFieldMaskSource : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create("notes"));
    }
}
