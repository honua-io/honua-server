// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Integration tests for table discovery with a separate PostGIS database connection.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.TableDiscovery)]
public sealed class ConnectionDiscoveryIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private Guid _connectionId;
    private string? _connectionDatabaseName;
    private string? _tableName;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;

        _connectionDatabaseName = await CreateConnectionDatabaseAsync();
        var connectionString = BuildConnectionString(_connectionDatabaseName);

        _tableName = $"table_{Guid.NewGuid():N}".ToLowerInvariant();
        await SeedConnectionDatabaseAsync(connectionString, _tableName);

        await CreateSecureConnectionAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_connectionId != Guid.Empty)
        {
            using var scope = _fixture.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
            await registry.DeleteConnectionAsync(_connectionId);
        }

        if (!string.IsNullOrWhiteSpace(_connectionDatabaseName))
        {
            await DropConnectionDatabaseAsync(_connectionDatabaseName);
        }

        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WithSeparatePostgisDatabase_ReturnsTablesFromConnection()
    {
        var response = await _client.GetAsync($"/api/v1/admin/connections/{_connectionId}/tables");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var tableDiscoveryResponse = JsonSerializer.Deserialize<Honua.Core.Features.Admin.Domain.TableDiscoveryResponse>(
            content, TableDiscoveryJsonContext.Default.TableDiscoveryResponse);

        tableDiscoveryResponse.Should().NotBeNull();
        tableDiscoveryResponse!.Tables.Should().Contain(table =>
            table.Schema == "public" && table.Table == _tableName);
    }

    private async Task CreateSecureConnectionAsync(string connectionString)
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host) ||
            string.IsNullOrWhiteSpace(builder.Database) ||
            string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException("Connection string is missing required connection details.");
        }

        var host = builder.Host!;
        var databaseName = builder.Database!;
        var username = builder.Username!;

        var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString);
        var keyVersion = await encryptionService.GetCurrentKeyVersionAsync();
        var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
        var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"separate-db-{Guid.NewGuid():N}",
            host: host,
            port: builder.Port,
            databaseName: databaseName,
            username: username,
            encryptedConnectionString: encrypted,
            encryptionKeyVersion: keyVersion,
            createdBy: "connection-discovery-test",
            description: "Separate PostGIS test connection",
            sslRequired: sslRequired,
            sslMode: sslMode);

        var created = await registry.CreateConnectionAsync(connection);
        _connectionId = created.ConnectionId;
    }

    private string BuildConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        };

        return builder.ConnectionString;
    }

    private async Task<string> CreateConnectionDatabaseAsync()
    {
        var databaseName = $"conn_{Guid.NewGuid():N}".ToLowerInvariant();
        var adminConnectionString = BuildAdminConnectionString();

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {databaseName};";
        await command.ExecuteNonQueryAsync();

        return databaseName;
    }

    private async Task SeedConnectionDatabaseAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE TABLE public.{tableName} (
                id SERIAL PRIMARY KEY,
                geom geometry(Point, 4326) NOT NULL
            );
            INSERT INTO public.{tableName} (geom)
            VALUES (ST_SetSRID(ST_Point(1, 1), 4326));
            """;

        await command.ExecuteNonQueryAsync();
    }

    private async Task DropConnectionDatabaseAsync(string databaseName)
    {
        var adminConnectionString = BuildAdminConnectionString();

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using (var terminateCommand = connection.CreateCommand())
        {
            terminateCommand.CommandText = """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @db_name
                  AND pid <> pg_backend_pid();
                """;
            terminateCommand.Parameters.AddWithValue("db_name", databaseName);
            await terminateCommand.ExecuteNonQueryAsync();
        }

        await using (var dropCommand = connection.CreateCommand())
        {
            dropCommand.CommandText = $"DROP DATABASE IF EXISTS {databaseName};";
            await dropCommand.ExecuteNonQueryAsync();
        }
    }

    private string BuildAdminConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };

        return builder.ConnectionString;
    }
}
