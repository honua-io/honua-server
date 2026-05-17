// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Seeding;

namespace Honua.Server.Tests.Seed;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class AdminSampleSeedFeatureServerTests : IAsyncLifetime
{
    private const string ServiceId = "admin_sample";
    private const int SitesLayerId = 3000;
    private const int RoutesLayerId = 3001;
    private const int AreasLayerId = 3002;
    private const double CoordinateTolerance = 0.0002;
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "server.yaml"));
        await _fixture.InitializeAsync();

        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await _fixture.Postgres.ApplySeedAsync(
            ResolveRepoFile("tests", "seed", "admin-sample-feature-server.yaml"),
            schema);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServerMetadata_WithAdminSampleSeed_ListsPreviewLayers()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{ServiceId}/FeatureServer?f=json");

        response.Be200Ok();
        using var document = await ReadJsonDocumentAsync(response);

        var layerIds = document.RootElement
            .GetProperty("layers")
            .EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetInt32())
            .ToArray();

        layerIds.Should().Contain([SitesLayerId, RoutesLayerId, AreasLayerId]);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnCountOnly=true")]
    public async Task FeatureServerCountQuery_WithAdminSampleSeed_ReturnsExpectedFeatureCounts()
    {
        await AssertCountAsync(SitesLayerId, 4);
        await AssertCountAsync(RoutesLayerId, 3);
        await AssertCountAsync(AreasLayerId, 2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServerGeoJsonQuery_WithProjectedAdminRoute_ReturnsWgs84Coordinates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{RoutesLayerId}/query" +
            "?where=objectid%20%3D%20300101&outFields=objectid,name,status&f=geojson");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
        using var document = await ReadJsonDocumentAsync(response);

        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);

        var geometry = features[0].GetProperty("geometry");
        geometry.GetProperty("type").GetString().Should().Be("LineString");

        var coordinates = geometry.GetProperty("coordinates");
        coordinates[0][0].GetDouble().Should().BeApproximately(-157.8583, CoordinateTolerance);
        coordinates[0][1].GetDouble().Should().BeApproximately(21.3069, CoordinateTolerance);
        coordinates[2][0].GetDouble().Should().BeApproximately(-157.9225, CoordinateTolerance);
        coordinates[2][1].GetDouble().Should().BeApproximately(21.3187, CoordinateTolerance);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnExtentOnly=true")]
    public async Task FeatureServerExtentQuery_WithAdminSampleSeed_ReturnsExpectedBounds()
    {
        await AssertExtentAsync(SitesLayerId, -158.0011, 21.3069, -157.7394, 21.3972);
        await AssertExtentAsync(RoutesLayerId, -158.0011, 21.3069, -157.7394, 21.3972);
        await AssertExtentAsync(AreasLayerId, -157.95, 21.29, -157.70, 21.43);
    }

    private async Task AssertCountAsync(int layerId, int expectedCount)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{layerId}/query?where=1%3D1&returnCountOnly=true&f=json");

        response.Be200Ok();
        using var document = await ReadJsonDocumentAsync(response);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(expectedCount);
    }

    private async Task AssertExtentAsync(
        int layerId,
        double expectedXmin,
        double expectedYmin,
        double expectedXmax,
        double expectedYmax)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{layerId}/query?where=1%3D1&returnExtentOnly=true&outSR=4326&f=json");

        response.Be200Ok();
        using var document = await ReadJsonDocumentAsync(response);
        var extent = document.RootElement.GetProperty("extent");
        extent.GetProperty("xmin").GetDouble().Should().BeApproximately(expectedXmin, CoordinateTolerance);
        extent.GetProperty("ymin").GetDouble().Should().BeApproximately(expectedYmin, CoordinateTolerance);
        extent.GetProperty("xmax").GetDouble().Should().BeApproximately(expectedXmax, CoordinateTolerance);
        extent.GetProperty("ymax").GetDouble().Should().BeApproximately(expectedYmax, CoordinateTolerance);
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private static string ResolveRepoFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test should run under the repository output tree");
        return Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray());
    }
}
