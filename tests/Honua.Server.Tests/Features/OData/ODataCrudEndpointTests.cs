// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataCrudEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Features({layerId})")]
    public async Task CreateFeature_WithValidPayload_ReturnsCreated()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "New City",
                ["population"] = 123456L
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
        created.Should().NotBeNull();
        created!.ObjectId.Should().BeGreaterThan(0);
        created.LayerId.Should().Be(TestLayerId);
        created.Attributes.Should().Contain("New City");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId})")]
    public async Task UpdateFeature_WithValidPayload_ReturnsUpdatedFeature()
    {
        var existingId = await InsertFeatureAsync("Original");

        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Updated City"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},{existingId})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var updated = JsonSerializer.Deserialize(responseContent, ODataJsonContext.Default.ODataFeatureResponse);
        updated.Should().NotBeNull();
        updated!.ObjectId.Should().Be(existingId);
        updated.Attributes.Should().Contain("Updated City");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features({layerId},{objectId})")]
    public async Task DeleteFeature_WithValidId_ReturnsNoContent()
    {
        var existingId = await InsertFeatureAsync("Delete Me");

        var response = await _fixture.Client.DeleteAsync($"/odata/Features({TestLayerId},{existingId})");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<long> InsertFeatureAsync(string name)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, NULL, jsonb_build_object('name', @name))
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layerId", TestLayerId);
        command.Parameters.AddWithValue("name", name);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
