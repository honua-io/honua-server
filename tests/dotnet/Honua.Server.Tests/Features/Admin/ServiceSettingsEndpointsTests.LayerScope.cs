// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ServiceSettingsEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_LayerScope_ReturnsItsLogicalService()
    {
        const string connection = "11111111-1111-1111-1111-111111111111";
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            StorageBindings = snapshot.Graph.StorageBindings.Select(binding => binding.StorageLayerId == 0
                ? binding with { ConnectionId = connection } : binding).ToArray(),
        }, schema: _fixture.MetadataGraphSchema);

        var response = await _client.GetAsync($"/api/v1/admin/services?connectionId={connection}&storageLayerId=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("total").GetInt32().Should().Be(1);
        data.GetProperty("items")[0].GetProperty("serviceName").GetString().Should().Be("test");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_UnknownLayerScope_ReturnsAnEmptyPage()
    {
        var response = await _client.GetAsync("/api/v1/admin/services?connectionId=11111111-1111-1111-1111-111111111111&storageLayerId=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("total").GetInt32().Should().Be(0);
        data.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ListServices_IncompleteLayerScope_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/services?storageLayerId=0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
