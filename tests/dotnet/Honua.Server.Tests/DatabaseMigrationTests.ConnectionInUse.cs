// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests;

public sealed partial class DatabaseMigrationTests
{
    // The connection delete route answers 409 only because migration 008's RESTRICT foreign key
    // refuses the registry's DELETE; the seeded test schema does not carry that key.
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task DbUpMigrations_ConnectionReferencedByService_RefusesRegistryDelete()
    {
        var migration = BuildCanonicalUpgrader().PerformUpgrade();
        migration.Successful.Should().BeTrue($"migrations should complete successfully. Error: {migration.Error}");

        await using var connection = await OpenSchemaConnectionAsync();
        var connectionId = Guid.NewGuid();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO honua.data_connections
                    (connection_id, name, host, database_name, username, connection_string_encrypted)
                VALUES (@id, 'in-use', 'db.example.com', 'gis', 'reader', '\x01'::bytea);
                INSERT INTO honua.services (service_name, connection_id) VALUES ('in_use_service', @id);
                """;
            seed.Parameters.AddWithValue("id", connectionId);
            await seed.ExecuteNonQueryAsync();
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM honua.data_connections WHERE connection_id = @id RETURNING name";
        delete.Parameters.AddWithValue("id", connectionId);
        var refusal = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => delete.ExecuteScalarAsync());

        // PostgreSQL 18 reports a RESTRICT key as restrict_violation; earlier servers as foreign_key_violation.
        refusal.SqlState.Should().BeOneOf(Npgsql.PostgresErrorCodes.RestrictViolation, Npgsql.PostgresErrorCodes.ForeignKeyViolation);
        await using var remaining = connection.CreateCommand();
        remaining.CommandText = "SELECT count(*) FROM honua.data_connections WHERE connection_id = @id";
        remaining.Parameters.AddWithValue("id", connectionId);
        ((long)(await remaining.ExecuteScalarAsync())!).Should().Be(1, "the referenced connection is kept");
    }

    // Every canonical-schema test here migrates its private schema the same way.
    private DbUp.Engine.UpgradeEngine BuildCanonicalUpgrader()
    {
        var connectionStringBuilder = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = $"{_schemaName},public"
        };
        return DeployChanges.To
            .PostgresqlDatabase(connectionStringBuilder.ToString(), _schemaName)
            .JournalToPostgresqlTable(_schemaName, "schema_versions")
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithVariable("HonuaSchema", "\"honua\"")
            .WithTransaction()
            .Build();
    }
}
