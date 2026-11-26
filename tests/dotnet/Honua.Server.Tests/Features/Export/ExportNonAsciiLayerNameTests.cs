// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Export;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ExportNonAsciiLayerNameTests : IAsyncLifetime
{
    private const string NonAsciiLayerName = "Café Zürich";
    private readonly WebAppFixture _fixture = new();
    private string _serviceName = string.Empty;
    private int _serviceLayerIndex;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        RenameSeededLayer(NonAsciiLayerName);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("csv", ".csv")]
    [InlineData("shapefile", ".zip")]
    [InlineData("gpkg", ".gpkg")]
    [InlineData("geoparquet", ".parquet")]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/export")]
    public async Task Export_NonAsciiLayerName_Returns200WithRfc5987ContentDisposition(string format, string expectedExtension)
    {
        using var client = _fixture.CreateAdminClient();
        var path = $"/api/v1/admin/services/{Uri.EscapeDataString(_serviceName)}/layers/{_serviceLayerIndex}/export?format={format}";

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.TryGetValues("Content-Disposition", out var headerValues).Should().BeTrue();
        var rawHeader = headerValues!.Should().ContainSingle().Subject;
        rawHeader.Should().StartWith("attachment;");
        rawHeader.All(char.IsAscii).Should().BeTrue("HTTP headers must be ASCII-only");
        rawHeader.Should().ContainEquivalentOf("filename*=utf-8''");
        rawHeader.Should().ContainEquivalentOf("Caf%C3%A9");
        rawHeader.Should().ContainEquivalentOf("Z%C3%BCrich");
        rawHeader.Should().Contain(expectedExtension);
        response.Content.Headers.ContentDisposition?.FileNameStar
            .Should().Be($"{_serviceName}_{NonAsciiLayerName}{expectedExtension}");
    }

    private void RenameSeededLayer(string layerName)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            snapshot.ResolveStorageLayerId(candidate) == WebAppFixture.TestLayerId &&
            snapshot.ResolveResource(candidate)?.Type == MetadataV2ObjectType.FeatureDataset);
        var resource = snapshot.ResolveResource(publication)!;
        _serviceName = snapshot.Index.ServicesById[publication.ServiceId].Metadata.Name;
        _serviceLayerIndex = publication.LayerIndex;

        var updatedGraph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = snapshot.Graph.Resources.Select(candidate => candidate.Metadata.Id == resource.Metadata.Id
                ? candidate with { Metadata = candidate.Metadata with { Name = layerName } }
                : candidate).ToArray(),
        };
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
            .SetGraph(updatedGraph, schema: _fixture.MetadataGraphSchema);
    }
}
