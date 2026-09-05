// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Collection("Database")]
public sealed class PostgresFixtureIdentifierTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("PostgresStudioPackageStoreTests_WithAnEvenLongerClassName")]
    [InlineData("É中文_'\";\n")]
    [InlineData("")]
    public async Task CreateIsolatedSchemaAsync_ArbitraryClassName_PreservesDistinctExactIdentifiers(string testClassName)
    {
        var first = await fixture.CreateIsolatedSchemaAsync(testClassName);
        try
        {
            var second = await fixture.CreateIsolatedSchemaAsync(testClassName);
            try
            {
                first.Should().NotBe(second);
                foreach (var schema in new[] { first, second })
                {
                    schema.Should().MatchRegex("\\Atest_[a-z0-9_]*_[a-f0-9]{32}\\z");
                    schema.Length.Should().BeLessThanOrEqualTo(63);
                    await using var connection = await fixture.DataSource.OpenConnectionAsync();
                    await SchemaSearchPath.ApplyAsync(connection, schema);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT current_schema()";
                    (await command.ExecuteScalarAsync()).Should().Be(schema);
                }
            }
            finally
            {
                await fixture.DropSchemaAsync(second);
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(first);
        }
    }

    [IntegrationTest]
    public async Task CreateIsolatedDatabaseAsync_LongClassName_ConnectsToExactIdentifier()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(
            "PostgresStudioPackageStoreTests_WithAnEvenLongerClassName");
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        try
        {
            database.Length.Should().BeLessThanOrEqualTo(63);
            database.Should().MatchRegex("\\Atest_[a-z0-9_]*_[a-f0-9]{32}\\z");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_database()";
            (await command.ExecuteScalarAsync()).Should().Be(database);
        }
        finally
        {
            await fixture.DropDatabaseAsync(database);
        }
    }
}
