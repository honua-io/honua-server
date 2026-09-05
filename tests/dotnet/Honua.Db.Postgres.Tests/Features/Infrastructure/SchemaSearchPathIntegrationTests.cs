// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Collection("Database")]
public sealed class SchemaSearchPathIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task ApplyAsync_UnsafeInputCannotChangeTheQuotedSearchPath()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await SchemaSearchPath.ApplyAsync(connection, "MixedCase");

        foreach (var unsafeSchema in new[]
        {
            "tenant\"; SELECT set_config('search_path', 'public', false); --",
            "MixedCase\n",
            new string('a', 63) + "_other_tenant"
        })
        {
            var action = () => SchemaSearchPath.ApplyAsync(connection, unsafeSchema);
            await action.Should().ThrowAsync<InvalidOperationException>();

            await using var command = connection.CreateCommand();
            command.CommandText = "SHOW search_path";
            (await command.ExecuteScalarAsync()).Should().Be("\"MixedCase\", public");
        }
    }
}
