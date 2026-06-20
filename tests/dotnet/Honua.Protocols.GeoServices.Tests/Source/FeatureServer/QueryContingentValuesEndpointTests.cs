// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the service-level <c>queryContingentValues</c> operation (#1878 Phase 1):
/// the endpoint serves per-layer contingent value definitions from the Metadata v2 graph and an empty
/// collection when none are authored.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class QueryContingentValuesEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static JsonElement Number(int value) => JsonSerializer.SerializeToElement(value);

    [IntegrationTest]
    [Operation(Operations.QueryContingentValues)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryContingentValues")]
    public async Task QueryContingentValues_NoneAuthored_ReturnsEmptyDefinitions()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryContingentValues?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("contingentValuesDefinitions").GetArrayLength().Should().Be(0);
        // The spec-shaped scaffolding is still present.
        root.GetProperty("typeCodes").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryContingentValues)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryContingentValues")]
    public async Task QueryContingentValues_GroupsAuthored_ServesPerLayerDefinitions()
    {
        var group = new MetadataV2ContingentValueGroup
        {
            Name = "material-diameter",
            Restrictive = true,
            Fields = ["material", "diameter"],
            ContingentValues =
            [
                new MetadataV2ContingentValue
                {
                    Id = 1,
                    Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["material"] = new() { Type = "code", Code = Number(10) },
                        ["diameter"] = new() { Type = "range", Range = [Number(0), Number(12)] },
                    },
                },
            ],
        };

        await _fixture.UpdateV2ResourceContingentValueGroupsAsync(WebAppFixture.TestLayerId, [group]);

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryContingentValues?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        var definitions = root.GetProperty("contingentValuesDefinitions");
        definitions.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var layerDefinition = definitions.EnumerateArray()
            .First(d => d.GetProperty("id").GetInt32() == WebAppFixture.TestLayerId);

        var fieldGroups = layerDefinition.GetProperty("fieldGroups");
        fieldGroups.GetArrayLength().Should().Be(1);

        var fieldGroup = fieldGroups[0];
        fieldGroup.GetProperty("name").GetString().Should().Be("material-diameter");
        fieldGroup.GetProperty("restrictive").GetBoolean().Should().BeTrue();
        fieldGroup.GetProperty("fields").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("material", "diameter");

        var row = fieldGroup.GetProperty("contingentValues")[0];
        row.GetProperty("id").GetInt32().Should().Be(1);
        row.GetProperty("values").GetProperty("material").GetProperty("type").GetString().Should().Be("code");
        row.GetProperty("values").GetProperty("diameter").GetProperty("type").GetString().Should().Be("range");

        // Clean up the authored groups so a shared fixture does not leak into other tests.
        await _fixture.UpdateV2ResourceContingentValueGroupsAsync(WebAppFixture.TestLayerId, null);
    }
}
