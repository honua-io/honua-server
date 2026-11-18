// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Styling;

// The layer's style classifies and filters on "notes", which the caller's mask withholds; served documents name neither.
[Collection("Database")]
public sealed class WithheldFieldStyleDocumentTests(WithheldFieldStyleDocumentFixture fixture)
    : IClassFixture<WithheldFieldStyleDocumentFixture>
{
    private const string WithheldClassValue = "Confidential";
    private const string WithheldFilterValue = "Restricted";

    private readonly WebAppFixture _app = fixture.App;

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/styles/{publicationId}.json")]
    public async Task MapLibreStyle_OmitsLayersAndPaintThatReadAMaskedField()
    {
        var body = await GetOkAsync($"/api/styles/{Uri.EscapeDataString(fixture.PublicationId)}.json");

        AssertNoWithheldReference(body);
        LayerIds(body).Should().Contain(WithheldFieldStyleDocumentFixture.ClassLayerId)
            .And.NotContain(WithheldFieldStyleDocumentFixture.FilterLayerId);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task OgcStylesheet_MapLibre_OmitsLayersAndPaintThatReadAMaskedField()
    {
        var body = await GetOkAsync($"/ogc/styles/{Uri.EscapeDataString(fixture.StyleId)}");

        AssertNoWithheldReference(body);
        LayerIds(body).Should().NotContain(WithheldFieldStyleDocumentFixture.FilterLayerId);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    [InlineData("application/vnd.ogc.sld+xml;version=1.0")]
    [InlineData("application/vnd.ogc.sld+xml;version=1.1")]
    [InlineData("application/vnd.esri.drawinginfo+json")]
    public async Task OgcStylesheet_DerivedEncodings_ListNoClassOfAMaskedField(string mediaType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(fixture.StyleId)}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(mediaType));
        using var response = await _app.CreateAdminClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(WithheldClassValue).And.NotContain(WithheldFilterValue);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.VectorTileServer)]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}")]
    public async Task VectorTileServerRootStyle_OmitsLayersAndPaintThatReadAMaskedField()
    {
        var body = await GetOkAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/VectorTileServer/resources/styles/root.json");

        AssertNoWithheldReference(body);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryContingentValues)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryContingentValues")]
    public async Task QueryContingentValues_OmitsGroupsOverAMaskedField()
    {
        var body = await GetOkAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryContingentValues?f=json");

        body.Should().Contain(WithheldFieldStyleDocumentFixture.VisibleGroupName)
            .And.NotContain(WithheldFieldStyleDocumentFixture.MaskedGroupName)
            .And.NotContain(WithheldFieldStyleDocumentFixture.MaskedField);
    }

    private static void AssertNoWithheldReference(string body)
        => body.Should().NotContain(WithheldClassValue)
            .And.NotContain(WithheldFilterValue)
            .And.NotContain($"\"{WithheldFieldStyleDocumentFixture.MaskedField}\"");

    private static string[] LayerIds(string styleJson)
    {
        using var document = JsonDocument.Parse(styleJson);
        return [.. document.RootElement.GetProperty("layers").EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetString()!)];
    }

    private async Task<string> GetOkAsync(string path)
    {
        using var response = await _app.CreateAdminClient().GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }
}

public sealed class WithheldFieldStyleDocumentFixture : IAsyncLifetime
{
    public const string MaskedField = "notes";
    public const string ClassLayerId = "notes-classes";
    public const string FilterLayerId = "notes-restricted";
    public const string MaskedGroupName = "notes-pairs";
    public const string VisibleGroupName = "material-diameter";

    public WebAppFixture App { get; } = new WebAppFixture()
        .ReplaceService<IFieldMaskSource>(new NotesFieldMaskSource());

    public string PublicationId { get; private set; } = string.Empty;

    public string StyleId { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
        await App.UpdateV2ResourceContingentValueGroupsAsync(WebAppFixture.TestLayerId, [Group(VisibleGroupName, "material"), Group(MaskedGroupName, MaskedField)]);
        await PutStyleAsync();
        var snapshot = App.GetCurrentV2GraphSnapshot();
        StyleId = snapshot.Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId].Metadata.Name;
        PublicationId = snapshot.Graph.Publications
            .First(publication => publication.LayerIndex == WebAppFixture.TestLayerId).Metadata.Id;
    }

    public Task DisposeAsync() => App.DisposeAsync();

    private async Task PutStyleAsync()
    {
        var style = JsonSerializer.SerializeToNode(StyleDefaults.BuildDefaultMapLibreStyle(
            new StyleLayerDescriptor(WebAppFixture.TestLayerId, "Test Layer", MetadataV2GeometryType.Point)))!.AsObject();
        var layers = style["layers"]!.AsArray();
        var template = layers[0]!.AsObject();
        layers.Add(StyleLayer(template, ClassLayerId, filter: null, color: JsonNode.Parse(
            """["match",["get","notes"],"Confidential","#d7191c","#2c7bb6"]""")!));
        layers.Add(StyleLayer(template, FilterLayerId, filter: JsonNode.Parse("""["==",["get","notes"],"Restricted"]""")!, color: "#000000"));

        using var content = JsonContent.Create(new { mapLibreStyle = style });
        using var response = await App.CreateAdminClient().PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style", content);
        response.EnsureSuccessStatusCode();
    }

    private static JsonObject StyleLayer(JsonObject template, string id, JsonNode? filter, JsonNode color)
    {
        var layer = new JsonObject
        {
            ["id"] = id,
            ["type"] = "circle",
            ["source"] = template["source"]?.DeepClone(),
            ["paint"] = new JsonObject { ["circle-radius"] = 4, ["circle-color"] = color },
        };
        if (template["source-layer"] is { } sourceLayer)
        {
            layer["source-layer"] = sourceLayer.DeepClone();
        }

        if (filter is not null)
        {
            layer["filter"] = filter;
        }

        return layer;
    }

    private static MetadataV2ContingentValueGroup Group(string name, string field) => new()
    {
        Name = name,
        Restrictive = true,
        Fields = [field, "diameter"],
        ContingentValues =
        [
            new MetadataV2ContingentValue
            {
                Id = 1,
                Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                {
                    [field] = new() { Type = "code", Code = JsonElement.Parse("\"steel\"") },
                    ["diameter"] = new() { Type = "code", Code = JsonElement.Parse("8") },
                },
            },
        ],
    };

    private sealed class NotesFieldMaskSource : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create(MaskedField));
    }
}
