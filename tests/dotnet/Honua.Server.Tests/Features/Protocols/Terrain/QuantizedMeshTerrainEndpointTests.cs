// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Scene.Terrain;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

[Collection("Database")]
[Protocol(TestProtocols.Terrain)]
public sealed class QuantizedMeshTerrainEndpointTests : IAsyncLifetime
{
    private const string ProblemJson = "application/problem+json";

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_OutsideCoverage_ReturnsFlatEllipsoidMesh()
    {
        await using var heldRasters = await TerrainTestRasters.HoldSouthEastQuadrantAsync(_fixture, 50);

        // Geographic TMS z1 x0 y1 spans lon -180..-90, lat 0..90; the seed covers only lon 0..180, lat below 0.
        var tile = await ReadMeshAsync("/terrain/0/1/0/1.terrain");

        tile.Header.MinimumHeight.Should().Be(0f);
        tile.Header.MaximumHeight.Should().Be(0f);
        tile.U.Length.Should().Be(TerrainTmsGrid.DefaultGridSize * TerrainTmsGrid.DefaultGridSize);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithSourceNoData_DoesNotEncodeNoDataAsHeight()
    {
        await using var heldRasters = await TerrainTestRasters.HoldAllSourceNoDataAsync(_fixture);

        var tile = await ReadMeshAsync("/terrain/0/0/0/0.terrain");

        tile.Header.MinimumHeight.Should().Be(0f, "a source NoData cell must not become a -9999 m pit");
        tile.Header.MaximumHeight.Should().Be(0f);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithCollectionName_ResolvesDataset()
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 77);

        var tile = await ReadMeshAsync("/terrain/Test%20Layer/0/1/0.terrain");

        tile.Header.MaximumHeight.Should().BeApproximately(77f, 0.5f);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithExtensionsQuery_ReturnsNormalsExtension()
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 250);

        var response = await _fixture.Client.GetAsync("/terrain/0/1/0/1.terrain?extensions=octvertexnormals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.Parameters
            .Should().Contain(parameter => parameter.Name == "extensions" && parameter.Value == "octvertexnormals");
        var tile = QuantizedMeshTileReader.Read(await response.Content.ReadAsByteArrayAsync());
        tile.OctEncodedNormals.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithNormalsRefusedByZeroQuality_OmitsNormalsExtension()
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 250);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/terrain/0/1/0/1.terrain");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(
            "application/vnd.quantized-mesh;extensions=octvertexnormals;q=0"));
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.quantized-mesh"));
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.Parameters.Should().NotContain(parameter => parameter.Name == "extensions");
        var tile = QuantizedMeshTileReader.Read(await response.Content.ReadAsByteArrayAsync());
        tile.OctEncodedNormals.Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("/terrain/0/23/0/0.terrain")]
    [InlineData("/terrain/0/0/-1/0.terrain")]
    [InlineData("/terrain/0/0/0/1.terrain")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithCoordinatesOutsideLimits_ReturnsBadRequest(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 1);

        var response = await _fixture.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be(ProblemJson);
    }

    [IntegrationTheory]
    [InlineData("/terrain/999999/0/0/0.terrain")]
    [InlineData("/terrain/no-such-collection/0/0/0.terrain")]
    [InlineData("/terrain/no-such-collection/layer.json")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetQuantizedMesh_WithUnknownDataset_ReturnsNotFound(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 1);

        var response = await _fixture.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be(ProblemJson);
    }

    [IntegrationTheory]
    [InlineData("/terrain/0/0/0/0.terrain")]
    [InlineData("/terrain/0/layer.json")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetQuantizedMesh_WhenTerrainProtocolDisabled_ReturnsNotFound(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 1);
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            enabledProtocols: TerrainTestRasters.ProtocolsWithoutTerrain,
            accessPolicy: anonymous);

        try
        {
            var response = await _fixture.Client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            _fixture.UpdateV2ServiceMetadata(
                WebAppFixture.TestServiceId,
                enabledProtocols: TerrainTestRasters.AllProtocols,
                accessPolicy: anonymous);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithUnknownSourceCrs_ReturnsUnprocessableEntity()
    {
        await using var heldRasters = await TerrainTestRasters.HoldUnknownCrsAsync(_fixture);

        await AssertCrsRefusedAsync("/terrain/0/0/0/0.terrain");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_WithUnregisteredPositiveSourceCrs_ReturnsUnprocessableEntity()
    {
        await using var heldRasters = await TerrainTestRasters.HoldUnregisteredCrsAsync(_fixture);

        await AssertCrsRefusedAsync("/terrain/0/0/0/0.terrain");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    public async Task GetTerrainLayerJson_WithCollectionName_AdvertisesCollectionTileTemplate()
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 12);

        var response = await _fixture.Client.GetAsync("/terrain/Test%20Layer/layer.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("tiles").EnumerateArray()
            .Select(static tile => tile.GetString())
            .Should().ContainSingle()
            .Which.Should().Contain("{z}/{x}/{y}.terrain");
        json.RootElement.GetProperty("scheme").GetString().Should().Be("tms");
        json.RootElement.GetProperty("projection").GetString().Should().Be("EPSG:4326");
    }

    private async Task AssertCrsRefusedAsync(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CRS");
    }

    private async Task<QuantizedMeshTile> ReadMeshAsync(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentEncoding.Should().Contain("gzip");
        return QuantizedMeshTileReader.Read(await response.Content.ReadAsByteArrayAsync());
    }
}
