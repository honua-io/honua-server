// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

public partial class SecureConnectionEndpointsTests
{
    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/connections/{id}")]
    public async Task DeleteConnection_ReferencedByService_ReturnsConflictAndKeepsConnection()
    {
        await InitializeDefaultApplicationAsync();
        var created = await _registry.CreateConnectionAsync(DataConnection.CreateWithEncryptedCredentials(
            name: $"test-delete-in-use-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: [9, 8, 7],
            encryptionKeyVersion: 1,
            createdBy: "test-user"));
        var serviceName = $"svc_in_use_{Guid.NewGuid():N}";
        await ExecuteServiceSqlAsync(
            "INSERT INTO honua.services (service_name, connection_id) VALUES (@service, @connection)",
            serviceName,
            created.ConnectionId);
        try
        {
            using var response = await _client.DeleteAsync($"/api/v1/admin/connections/{created.ConnectionId}");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("in use", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.NotNull(await _registry.GetConnectionAsync(created.ConnectionId));
        }
        finally
        {
            await ExecuteServiceSqlAsync(
                "DELETE FROM honua.services WHERE service_name = @service AND connection_id = @connection",
                serviceName,
                created.ConnectionId);
            await _registry.DeleteConnectionAsync(created.ConnectionId);
        }
    }

    private async Task ExecuteServiceSqlAsync(string sql, string serviceName, Guid connectionId)
    {
        var dataSource = _fixture.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = dataSource.CreateCommand(sql);
        _ = command.Parameters.AddWithValue("service", serviceName);
        _ = command.Parameters.AddWithValue("connection", connectionId);
        _ = await command.ExecuteNonQueryAsync();
    }
}
