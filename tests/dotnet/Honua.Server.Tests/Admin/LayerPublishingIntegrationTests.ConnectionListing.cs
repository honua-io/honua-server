// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
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
    [IntegrationTheory]
    [InlineData("source")]
    [InlineData("managed")]
    [InlineData("mixed")]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Query)]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task ListConnectionLayers_SameDatabaseAlias_DoesNotDuplicatePublishedLayers(string scenario)
    {
        await UseServerFeatureStoreConnectionAsync();
        using var connections = await _client.GetAsync("/api/v1/admin/connections");
        connections.StatusCode.Should().Be(HttpStatusCode.OK);
        int? sourceId = scenario == "mixed" ? await PublishConnectionListingLayerAsync("source") : null;
        var storageMode = scenario == "source" ? "source" : "managed";
        _layerId = await PublishConnectionListingLayerAsync(storageMode);
        var ownerId = _connectionId;
        var aliasId = await CreateConnectionListingAliasAsync();
        try
        {
            await AssertConnectionLayerPageAsync(ownerId, sourceId ?? (scenario == "source" ? _layerId : null));
            await AssertConnectionLayerPageAsync(ManagedDataConnection.Id, scenario == "source" ? null : _layerId);
            await AssertConnectionLayerPageAsync(aliasId, null);
            var emptyPage = await ReadConnectionLayerPageAsync(aliasId, page: 2);
            emptyPage.Total.Should().Be(0);
            emptyPage.Items.Should().BeEmpty();
        }
        finally
        {
            await DeleteSecureConnectionAsync(aliasId);
        }
    }

    private async Task<int> PublishConnectionListingLayerAsync(string storageMode)
    {
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"{storageMode} {_tableName}",
            GeometryColumn = "geom",
            PrimaryKey = "id",
            ServiceName = _serviceName,
            StorageMode = storageMode,
            GeometryType = "Point",
            Srid = 4326,
            Fields = _idNamePopulationFields
        });
        return published.StorageLayerId;
    }

    private async Task<Guid> CreateConnectionListingAliasAsync()
    {
        var ownerId = _connectionId;
        var ownerName = _connectionName;
        await CreateSecureConnectionAsync(_fixture.Postgres.ConnectionString);
        var aliasId = _connectionId;
        _connectionId = ownerId;
        _connectionName = ownerName;
        return aliasId;
    }

    private async Task AssertConnectionLayerPageAsync(Guid connectionId, int? expectedLayerId)
    {
        var page = await ReadConnectionLayerPageAsync(connectionId, page: 1);
        page.Total.Should().Be(expectedLayerId.HasValue ? 1 : 0);
        if (expectedLayerId.HasValue)
        {
            page.Items.Should().ContainSingle().Which.StorageLayerId.Should().Be(expectedLayerId.Value);
        }
        else
        {
            page.Items.Should().BeEmpty();
        }
    }

    private async Task<PublishedLayerPage> ReadConnectionLayerPageAsync(Guid connectionId, int page)
    {
        using var response = await _client.GetAsync(
            $"/api/v1/admin/connections/{connectionId}/layers?serviceName={_serviceName}&page={page}&pageSize=1");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<ApiResponse<PublishedLayerPage>>(body, _jsonOptions)!.Data!;
    }
}
