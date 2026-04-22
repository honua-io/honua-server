// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Tiles;

[Collection("Database")]
[Protocol(Protocols.Mvt)]
public sealed class TileJsonEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task GetTileJson_ReturnsTileJsonMetadata()
    {
        var layerId = WebAppFixture.TestLayerId;

        var response = await _fixture.Client.GetAsync($"/tiles/{layerId}/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var root = json.RootElement;
        root.GetProperty("tilejson").GetString().Should().Be("3.0.0");
        root.GetProperty("tiles").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("minzoom").GetInt32().Should().BeGreaterOrEqualTo(0);
        root.GetProperty("maxzoom").GetInt32().Should().BeGreaterOrEqualTo(root.GetProperty("minzoom").GetInt32());
        root.GetProperty("bounds").GetArrayLength().Should().Be(4);
        root.GetProperty("vector_layers").GetArrayLength().Should().BeGreaterThan(0);

        var vectorLayer = root.GetProperty("vector_layers")[0];
        vectorLayer.GetProperty("id").GetString().Should().Be("layer");
        var fields = vectorLayer.GetProperty("fields");
        fields.TryGetProperty("name", out var nameField).Should().BeTrue();
        nameField.GetString().Should().NotBeNullOrWhiteSpace();

        var styleUrl = root.GetProperty("style").GetString();
        styleUrl.Should().NotBeNullOrWhiteSpace();

        var styleResponse = await GetStyleFromTileJsonAsync(styleUrl!);
        styleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task GetTileJson_ValidatesAgainstSchema()
    {
        var layerId = WebAppFixture.TestLayerId;

        var response = await _fixture.Client.GetAsync($"/tiles/{layerId}/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();

        var schemaPath = ResolveSchemaPath(Path.Combine("tests", "Honua.Server.Tests", "TestData", "tilejson-3.0.schema.json"));
        var schemaJson = await File.ReadAllTextAsync(schemaPath);
        var schema = JSchema.Parse(schemaJson);

        var token = JToken.Parse(content);
        var isValid = token.IsValid(schema, out IList<ValidationError> errors);
        isValid.Should().BeTrue($"TileJSON schema validation failed: {FormatSchemaErrors(errors)}");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task MapLibre_CanLoadFromTileJson()
    {
        var layerId = WebAppFixture.TestLayerId;

        var tileJsonResponse = await _fixture.Client.GetAsync($"/tiles/{layerId}/tile.json");
        tileJsonResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tileJsonContent = await tileJsonResponse.Content.ReadAsStringAsync();
        using var tileJsonDoc = JsonDocument.Parse(tileJsonContent);

        var root = tileJsonDoc.RootElement;
        var minZoom = root.TryGetProperty("minzoom", out var minZoomElement) ? minZoomElement.GetInt32() : 0;
        var tileTemplate = root.GetProperty("tiles")[0].GetString();
        tileTemplate.Should().NotBeNullOrWhiteSpace();

        var styleUrl = root.GetProperty("style").GetString();
        styleUrl.Should().NotBeNullOrWhiteSpace();

        var styleResponse = await GetStyleFromTileJsonAsync(styleUrl!);
        styleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var styleContent = await styleResponse.Content.ReadAsStringAsync();
        using var styleDoc = JsonDocument.Parse(styleContent);

        var styleTileTemplate = TryGetTileTemplateFromStyle(styleDoc.RootElement);
        styleTileTemplate.Should().NotBeNullOrWhiteSpace();

        NormalizeTemplate(tileTemplate!).Should().Be(NormalizeTemplate(styleTileTemplate!));

        var tileUri = BuildTileUri(tileTemplate!, minZoom, 0, 0);
        var tileResponse = await _fixture.Client.GetAsync(tileUri);
        tileResponse.IsSuccessStatusCode.Should().BeTrue();
        if (tileResponse.StatusCode == HttpStatusCode.OK)
        {
            tileResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/styles/{layerId}.json")]
    public async Task GetStyle_ReturnsMapLibreStyle()
    {
        var response = await _fixture.Client.GetAsync($"/api/styles/{WebAppFixture.TestLayerId}.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var root = json.RootElement;
        root.GetProperty("version").GetInt32().Should().Be(8);
        root.GetProperty("sources").ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);
    }

    private Task<HttpResponseMessage> GetStyleFromTileJsonAsync(string styleUrl)
    {
        if (Uri.TryCreate(styleUrl, UriKind.Absolute, out var absolute))
        {
            return _fixture.Client.GetAsync(absolute);
        }

        var baseAddress = _fixture.Client.BaseAddress ?? new Uri("http://localhost");
        var resolved = new Uri(baseAddress, styleUrl);
        return _fixture.Client.GetAsync(resolved);
    }

    private static string ResolveSchemaPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || File.Exists(relativePath))
        {
            return relativePath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return relativePath;
    }

    private static string FormatSchemaErrors(IList<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return "no errors reported";
        }

        var builder = new StringBuilder();
        foreach (var error in errors)
        {
            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }
            builder.Append(error.Message);
        }

        return builder.ToString();
    }

    private static string? TryGetTileTemplateFromStyle(JsonElement root)
    {
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var source in sources.EnumerateObject())
        {
            var sourceDefinition = source.Value;
            if (!sourceDefinition.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            if (!string.Equals(typeElement.GetString(), "vector", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sourceDefinition.TryGetProperty("tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array && tiles.GetArrayLength() > 0)
            {
                return tiles[0].GetString();
            }
        }

        return null;
    }

    private Uri BuildTileUri(string template, int z, int x, int y)
    {
        var url = template
            .Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        var baseAddress = _fixture.Client.BaseAddress ?? new Uri("http://localhost");
        return new Uri(baseAddress, url);
    }

    private static string NormalizeTemplate(string template)
    {
        if (Uri.TryCreate(template, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery;
        }

        return template;
    }
}
