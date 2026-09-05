// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesCoreContractTests : IAsyncLifetime
{
    private const int TestLayerId = 0;
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<IOptions<LimitsOptions>>(Options.Create(new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxOffset = 1,
            },
        }));

        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(
            TestLayerId,
            spatial: new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.None,
            },
            clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_AtMaximumOffset_DoesNotAdvertiseUnreachableNextLink()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?limit=1&offset=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("links").EnumerateArray()
            .Should().NotContain(link => link.GetProperty("rel").GetString() == RelationTypes.Next);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithBboxOnNonSpatialCollection_MatchesAllFeatures()
    {
        var expectedIds = await GetReturnedFeatureIdsAsync(
            $"/ogc/features/collections/{TestLayerId}/items");

        var actualIds = await GetReturnedFeatureIdsAsync(
            $"/ogc/features/collections/{TestLayerId}/items?bbox=0,0,1,1");

        actualIds.Should().Equal(expectedIds);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithDatetimeOnNonTemporalCollection_MatchesAllFeatures()
    {
        var expectedIds = await GetReturnedFeatureIdsAsync(
            $"/ogc/features/collections/{TestLayerId}/items");

        var actualIds = await GetReturnedFeatureIdsAsync(
            $"/ogc/features/collections/{TestLayerId}/items?datetime=1900-01-01T00:00:00Z");

        actualIds.Should().Equal(expectedIds);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/api")]
    public async Task GetOpenApiSpec_ItemsParametersMatchRuntimeContract()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parameters = json.RootElement
            .GetProperty("paths")
            .GetProperty("/collections/{collectionId}/items")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, StringComparer.Ordinal);

        parameters["limit"].GetProperty("schema").GetProperty("maximum").GetInt32()
            .Should().Be(new LimitsOptions().Query.MaxRecordCount);
        parameters.Keys.Should().Contain(["ids", "properties", "sortby"]);
    }

    private async Task<string[]> GetReturnedFeatureIdsAsync(string requestUri)
    {
        var response = await _fixture.Client.GetAsync(requestUri);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").ToString())
            .ToArray();
    }
}
