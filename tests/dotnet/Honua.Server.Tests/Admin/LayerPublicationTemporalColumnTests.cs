// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    // honua-server#7434: a publish request can name the temporal column, so the published
    // layer reports temporal history instead of only the no-history state.
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities")]
    [Endpoint("GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of")]
    public async Task PublishWithTemporalColumn_ReportsHistoryAndServesAsOfReads()
    {
        await AddTemporalSourceColumnsAsync();

        var response = await PublishWithTemporalColumnAsync("VALID_FROM");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);
        _layerId = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions)!.Data!.StorageLayerId;

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var binding = snapshot.Graph.StorageBindings.Single(candidate => candidate.StorageLayerId == _layerId);
        binding.Options!["temporalColumn"].GetString().Should().Be("valid_from");

        var capability = await _client.GetAsync(
            $"/api/v1/temporal/services/{_serviceName}/layers/{_layerId}/capabilities");
        var capabilityPayload = await capability.Content.ReadAsStringAsync();
        capability.StatusCode.Should().Be(HttpStatusCode.OK, capabilityPayload);
        using (var document = JsonDocument.Parse(capabilityPayload))
        {
            document.RootElement.GetProperty("supportsHistory").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("supportsAsOf").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("temporalColumn").GetString().Should().Be("valid_from");
        }

        var asOf = await _client.GetAsync(
            $"/api/v1/temporal/services/{_serviceName}/layers/{_layerId}/as-of?generation=0");
        var asOfPayload = await asOf.Content.ReadAsStringAsync();
        asOf.StatusCode.Should().Be(HttpStatusCode.OK, asOfPayload);
    }

    [IntegrationTheory]
    [InlineData("missing_column")]
    [InlineData("label")]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishWithTemporalColumn_RejectsUnpublishedOrNonTemporalColumn(string temporalColumn)
    {
        await AddTemporalSourceColumnsAsync();

        var response = await PublishWithTemporalColumnAsync(temporalColumn);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        payload.Should().Contain("TemporalColumn");

        _fixture.GetCurrentV2GraphSnapshot().Graph.StorageBindings
            .Should().NotContain(candidate => candidate.Locator != null && candidate.Locator.Contains(_tableName));
    }

    private async Task AddTemporalSourceColumnsAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"ALTER TABLE public.{_tableName} ADD COLUMN valid_from timestamptz NOT NULL DEFAULT now(), ADD COLUMN label text",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task<HttpResponseMessage> PublishWithTemporalColumnAsync(string temporalColumn)
        => _client.PostAsync($"/api/v1/admin/connections/{_connectionId}/layers",
            JsonContent.Create(new PublishLayerRequest
            {
                Schema = _schema,
                Table = _tableName,
                LayerName = _tableName,
                GeometryColumn = "geom",
                GeometryType = "Point",
                Srid = 4326,
                PrimaryKey = "id",
                TemporalColumn = temporalColumn,
                ServiceName = _serviceName
            }, options: _jsonOptions));
}
