// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Authorization;

[Collection("Database")]
public sealed class ConsoleAccessVocabularyMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_ExistingOwnershipAndCanonicalCollision_AddsEnvironmentGrantAndKeepsExistingGrants()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ConsoleAccessVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, Seed);
            var identity = await ScalarAsync(schema, IdentityFingerprint);
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);
            (await ScalarAsync(schema, IdentityFingerprint)).Should().Be(identity);
            (await ScalarAsync(schema, """
                SELECT string_agg(service || ':' || layer || ':' || operation, ',' ORDER BY service COLLATE "C", layer COLLATE "C", operation COLLATE "C")
                FROM "{schema}".rbac_role_permissions
                """)).Should().Be("Console-Access:beta:environment-membership,Console-Access:beta:workspace-membership,console-access:alpha:environment-membership,console-access:alpha:query,console-access:alpha:workspace-membership,other:alpha:workspace-membership");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var directory = RepositoryPaths.Resolve("src", "Honua.Server", "Migrations");
        var path = Directory.EnumerateFiles(directory, "*_AddConsoleAccessEnvironmentOwnership.sql").Single();
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path))
            .Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private const string IdentityFingerprint = """
        SELECT (SELECT string_agg(role_id::text || ':' || name, ',' ORDER BY role_id) FROM "{schema}".rbac_roles)
            || '/' || (SELECT string_agg(role_id::text || ':' || user_id, ',' ORDER BY role_id, user_id)
                      FROM "{schema}".rbac_role_memberships)
        """;

    private const string Seed = """
        CREATE TABLE "{schema}".rbac_roles (role_id uuid PRIMARY KEY, name text NOT NULL);
        CREATE TABLE "{schema}".rbac_role_memberships (
            role_id uuid REFERENCES "{schema}".rbac_roles, user_id text, PRIMARY KEY (role_id, user_id));
        CREATE TABLE "{schema}".rbac_role_permissions (
            role_id uuid REFERENCES "{schema}".rbac_roles, service text, layer text, operation text,
            UNIQUE (role_id, service, layer, operation));
        INSERT INTO "{schema}".rbac_roles VALUES ('00000000-0000-0000-0000-000000000004', 'planners');
        INSERT INTO "{schema}".rbac_role_memberships VALUES ('00000000-0000-0000-0000-000000000004', 'member');
        INSERT INTO "{schema}".rbac_role_permissions VALUES
            ('00000000-0000-0000-0000-000000000004', 'console-access', 'alpha', 'workspace-membership'),
            ('00000000-0000-0000-0000-000000000004', 'console-access', 'alpha', 'environment-membership'),
            ('00000000-0000-0000-0000-000000000004', 'Console-Access', 'beta', 'workspace-membership'),
            ('00000000-0000-0000-0000-000000000004', 'console-access', 'alpha', 'query'),
            ('00000000-0000-0000-0000-000000000004', 'other', 'alpha', 'workspace-membership');
        """;
}
