// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    // services.connection_id stays empty when a layer joins an existing service, so the delete
    // guard must read the layer's storage binding, not only the services foreign key.
    [IntegrationTest]
    [Operation(Operations.Create, Operations.Delete)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("DELETE /api/v1/admin/connections/{id}")]
    public async Task DeleteConnection_WithPublishedLayerAndNoServiceLink_IsRefusedAndKept()
    {
        var layer = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = "Delete guard",
            ServiceName = _serviceName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNamePopulationFields,
            StorageMode = "source",
            Enabled = true
        });
        _layerId = layer.StorageLayerId;
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        {
            await using var unlink = connection.CreateCommand();
            unlink.CommandText = "UPDATE honua.services SET connection_id = NULL WHERE service_name = @service";
            unlink.Parameters.AddWithValue("service", _serviceName);
            await unlink.ExecuteNonQueryAsync();
        }

        using var response = await _client.DeleteAsync($"/api/v1/admin/connections/{_connectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        (await registry.GetConnectionAsync(_connectionId)).Should().NotBeNull("the connection backs a published layer");
    }
}
