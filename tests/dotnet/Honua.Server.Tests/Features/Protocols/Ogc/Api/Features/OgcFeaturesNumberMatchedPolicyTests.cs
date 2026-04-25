// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesNumberMatchedPolicyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OgcFeatures:NumberMatchedPolicy"] = "OmitWhenExpensive",
                    ["OgcFeatures:IncludeFeatureLinks"] = "false"
                })));

    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithOmittedNumberMatchedPolicy_OmitsNumberMatchedForPagedResponses()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();

        features.Length.Should().BeLessThanOrEqualTo(2);
        json.RootElement.TryGetProperty("numberMatched", out _).Should().BeFalse();
        json.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(features.Length);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithFeatureLinksDisabled_OmitsPerFeatureLinks()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestLayerId}/items?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var feature = json.RootElement.GetProperty("features")[0];

        feature.TryGetProperty("links", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("links", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithOmittedNumberMatchedPolicy_DoesNotDuplicateTopLevelIdInProperties()
    {
        var featureId = await _fixture.InsertFeatureAsync(TestLayerId, "Raw Path ID");

        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?limit=1&ids={featureId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = json.RootElement.GetProperty("features").EnumerateArray().Single();
        var properties = feature.GetProperty("properties");

        feature.GetProperty("id").GetInt64().Should().Be(featureId);
        properties.TryGetProperty("id", out _).Should().BeFalse();
        properties.TryGetProperty("objectid", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithRawPath_FiltersPropertiesToLayerSchema()
    {
        var featureId = await InsertFeatureWithExtraAttributesAsync("Raw Path Schema Filter");

        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items?limit=1&ids={featureId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = json.RootElement.GetProperty("features").EnumerateArray().Single();
        var properties = feature.GetProperty("properties");

        properties.GetProperty("name").GetString().Should().Be("Raw Path Schema Filter");
        properties.TryGetProperty("internal_secret", out _).Should().BeFalse();
        properties.TryGetProperty("objectid", out _).Should().BeFalse();
    }

    private async Task<long> InsertFeatureWithExtraAttributesAsync(string name)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, NULL, jsonb_build_object('name', @name, 'internal_secret', 'hidden', 'objectid', 12345))
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layerId", TestLayerId);
        command.Parameters.AddWithValue("name", name);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
