// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    // A publish request can name the temporal column, so the published layer reports
    // temporal history instead of only the no-history state.
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
    [InlineData("missing_column", null)]
    [InlineData("label", null)]
    [InlineData("opens_at", null)]
    [InlineData("valid_from", new[] { "id", "name" })]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishWithTemporalColumn_RejectsUnpublishedOrNonTemporalColumn(
        string temporalColumn, string[]? fields)
    {
        await AddTemporalSourceColumnsAsync();

        var response = await PublishWithTemporalColumnAsync(temporalColumn, fields);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        payload.Should().Contain("TemporalColumn");

        // Nothing was committed: the same table publishes once the column is valid.
        var retry = await PublishWithTemporalColumnAsync("valid_from", fields: null);
        var retryPayload = await retry.Content.ReadAsStringAsync();
        retry.StatusCode.Should().Be(HttpStatusCode.Created, retryPayload);
        _layerId = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(retryPayload, _jsonOptions)!.Data!.StorageLayerId;
    }

    private Task AddTemporalSourceColumnsAsync()
        => _fixture.Postgres.ExecuteDdlUnderLockAsync(
            $"ALTER TABLE public.{_tableName} ADD COLUMN valid_from timestamptz NOT NULL DEFAULT now(), "
            + "ADD COLUMN label text, ADD COLUMN opens_at time");

    private Task<HttpResponseMessage> PublishWithTemporalColumnAsync(string temporalColumn, string[]? fields = null)
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
                Fields = fields ?? [],
                ServiceName = _serviceName
            }, options: _jsonOptions));
}
