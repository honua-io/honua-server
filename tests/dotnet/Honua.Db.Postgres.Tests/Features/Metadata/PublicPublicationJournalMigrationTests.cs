// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

[Collection("Database")]
public sealed class PublicPublicationJournalMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public Task Migration_Reapplied_RetainsUnadvertisedCleanupObligations() => InSchemaAsync(async connection =>
    {
        await ReserveAsync(connection);
        await ApplyAsync(connection);
        await using var command = new NpgsqlCommand("""
            SELECT state, destination_bucket, metadata_revision, cdn_distribution_id, destination_base_url
            FROM public_publication_generations
            WHERE environment = 'production' AND publication_id = 'publication-a' AND version = 'version-a'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("pending");
        reader.GetString(1).Should().Be("original-public-bucket");
        reader.GetInt64(2).Should().Be(42);
        reader.GetString(3).Should().Be("original-distribution");
        reader.GetString(4).Should().Be("https://cdn.example.test");
    });

    [IntegrationTest]
    public Task Reserve_SameGeneration_RejectsReplacementOfCleanupIdentity() => InSchemaAsync(async connection =>
    {
        await ReserveAsync(connection);
        var duplicate = () => ReserveAsync(connection);
        await duplicate.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UniqueViolation);
    });

    [IntegrationTheory]
    [InlineData("ready")]
    [InlineData("revoked")]
    public Task Transition_WithoutCompletionEvidence_RejectsTerminalClaim(string state) => InSchemaAsync(async connection =>
    {
        await ReserveAsync(connection);
        await using var command = new NpgsqlCommand(
            "UPDATE public_publication_generations SET state = @state", connection);
        command.Parameters.AddWithValue("state", state);
        var transition = async () => await command.ExecuteNonQueryAsync();
        await transition.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
    });

    [IntegrationTest]
    public Task Revocation_OriginDeletionWithoutCompletedPurge_RetainsObligation() => InSchemaAsync(async connection =>
    {
        await ReserveAsync(connection);
        await using (var submitted = new NpgsqlCommand("""
            UPDATE public_publication_generations
            SET state = 'revoking', origin_deleted_at = now(), purge_ids = ARRAY['provider-invalidation-a']
            """, connection))
        {
            await submitted.ExecuteNonQueryAsync();
        }
        await using var premature = new NpgsqlCommand("""
            UPDATE public_publication_generations SET state = 'revoked', revoked_at = now()
            """, connection);
        var completion = async () => await premature.ExecuteNonQueryAsync();
        await completion.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
        await using var retained = new NpgsqlCommand(
            "SELECT state FROM public_publication_generations WHERE purge_ids = ARRAY['provider-invalidation-a']", connection);
        (await retained.ExecuteScalarAsync()).Should().Be("revoking");
    });

    private static async Task ReserveAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public_publication_generations
                (environment, publication_id, version, service_id, layer_index, metadata_revision,
                 destination_bucket, destination_base_url, cdn_distribution_id)
            VALUES ('production', 'publication-a', 'version-a', 'service-a', 0, 42,
                    'original-public-bucket', 'https://cdn.example.test', 'original-distribution')
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InSchemaAsync(Func<NpgsqlConnection, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PublicPublicationJournalMigrationTests));
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var searchPath = new NpgsqlCommand($"SET search_path TO \"{schema}\"", connection))
            {
                await searchPath.ExecuteNonQueryAsync();
            }
            await ApplyAsync(connection);
            await test(connection);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task ApplyAsync(NpgsqlConnection connection)
    {
        var path = RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", "213_CreatePublicPublicationGenerations.sql");
        var sql = await File.ReadAllTextAsync(path);
        await using var currentSchema = new NpgsqlCommand("SELECT current_schema()", connection);
        var schema = (string)(await currentSchema.ExecuteScalarAsync())!;
        using var builder = new NpgsqlCommandBuilder();
        var quotedSchema = builder.QuoteIdentifier(schema);
        await using var command = new NpgsqlCommand(sql.Replace("$HonuaSchema$", quotedSchema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
