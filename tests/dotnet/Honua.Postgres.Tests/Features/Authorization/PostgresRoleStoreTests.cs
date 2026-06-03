// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Authorization;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Authorization;

/// <summary>
/// Integration tests for <see cref="PostgresRoleStore"/> (#1374) using the shared
/// Testcontainers Postgres fixture. Exercises CRUD parity with the in-memory
/// store, built-in role protection, multi-role effective-permission resolution,
/// and restart durability against an isolated per-test schema mirroring
/// migration 041.
/// </summary>
[Collection("Database")]
public sealed class PostgresRoleStoreTests(PostgresFixture fixture)
{
    private static readonly Guid AdminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewerRoleId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [IntegrationTest]
    public async Task ListRoles_ReturnsSeededBuiltInRoles()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            await EnsureRbacTablesAsync(schema);
            var store = CreateStore(schema);

            var roles = await store.ListRolesAsync();

            roles.Select(r => r.Name).Should().Contain(["admin", "editor", "viewer"]);
            roles.Where(r => r.IsBuiltIn).Should().HaveCountGreaterThanOrEqualTo(3);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CreateRole_PersistsRoleAndGrants_AndIsReadable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            await EnsureRbacTablesAsync(schema);
            var store = CreateStore(schema);

            var role = new RoleDefinition
            {
                RoleId = Guid.NewGuid(),
                Name = "field-worker",
                Description = "Field collection editor",
                IsBuiltIn = false,
                Permissions =
                [
                    new PermissionGrant { Service = "parcels", Layer = "*", Operation = "query" },
                    new PermissionGrant { Service = "parcels", Layer = "edits", Operation = "update" },
                ],
            };

            await store.CreateRoleAsync(role);

            var fetched = await store.GetRoleAsync(role.RoleId);
            fetched.Should().NotBeNull();
            fetched!.Name.Should().Be("field-worker");
            fetched.Permissions.Should().HaveCount(2);
            fetched.Permissions.Should().ContainSingle(p =>
                p.Service == "parcels" && p.Layer == "edits" && p.Operation == "update");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DeleteRole_BuiltIn_IsRejected_NonBuiltIn_Succeeds()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            await EnsureRbacTablesAsync(schema);
            var store = CreateStore(schema);

            // Built-in role cannot be deleted.
            var deletedBuiltIn = await store.DeleteRoleAsync(AdminRoleId);
            deletedBuiltIn.Should().BeFalse();
            (await store.GetRoleAsync(AdminRoleId)).Should().NotBeNull();

            // A custom role can be deleted.
            var custom = new RoleDefinition { RoleId = Guid.NewGuid(), Name = "temp", IsBuiltIn = false };
            await store.CreateRoleAsync(custom);
            (await store.DeleteRoleAsync(custom.RoleId)).Should().BeTrue();
            (await store.GetRoleAsync(custom.RoleId)).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SetPermissions_ReplacesGrants()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            await EnsureRbacTablesAsync(schema);
            var store = CreateStore(schema);

            var role = new RoleDefinition
            {
                RoleId = Guid.NewGuid(),
                Name = "analyst",
                IsBuiltIn = false,
                Permissions = [new PermissionGrant { Service = "a", Layer = "*", Operation = "query" }],
            };
            await store.CreateRoleAsync(role);

            var replaced = await store.SetPermissionsAsync(role.RoleId,
            [
                new PermissionGrant { Service = "b", Layer = "*", Operation = "query" },
                new PermissionGrant { Service = "b", Layer = "*", Operation = "export" },
            ]);

            replaced.Should().HaveCount(2);
            var grants = await store.GetPermissionsAsync(role.RoleId);
            grants.Should().HaveCount(2);
            grants.Should().NotContain(p => p.Service == "a");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetEffectivePermissions_ResolvesAcrossMultipleRoles_Deduplicated()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            await EnsureRbacTablesAsync(schema);
            var store = CreateStore(schema);

            // viewer (seeded) grants query+metadata; add a custom role granting export.
            var exporter = new RoleDefinition
            {
                RoleId = Guid.NewGuid(),
                Name = "exporter",
                IsBuiltIn = false,
                Permissions =
                [
                    new PermissionGrant { Service = "*", Layer = "*", Operation = "export" },
                    // duplicate of a viewer grant — must be de-duplicated.
                    new PermissionGrant { Service = "*", Layer = "*", Operation = "query" },
                ],
            };
            await store.CreateRoleAsync(exporter);

            var effective = await store.GetEffectivePermissionsAsync("user-1", ["viewer", "exporter"]);

            effective.Permissions.Select(p => p.Operation).Should().Contain(["query", "metadata", "export"]);
            effective.Permissions.Where(p => p.Operation == "query").Should().HaveCount(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Roles_SurviveStoreReinstantiation()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            await EnsureRbacTablesAsync(schema);

            var roleId = Guid.NewGuid();
            var first = CreateStore(schema);
            await first.CreateRoleAsync(new RoleDefinition
            {
                RoleId = roleId,
                Name = "durable",
                IsBuiltIn = false,
                Permissions = [new PermissionGrant { Service = "*", Layer = "*", Operation = "query" }],
            });

            // A brand-new store instance (simulating a process restart / second
            // node) must see the persisted role and its grants.
            var second = CreateStore(schema);
            var fetched = await second.GetRoleAsync(roleId);

            fetched.Should().NotBeNull();
            fetched!.Name.Should().Be("durable");
            fetched.Permissions.Should().ContainSingle();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetEffectivePermissions_RbacTablesAbsent_ReturnsNoGrants_DoesNotThrow()
    {
        // Regression (#1385): integration-test fixtures and fresh/legacy DBs that
        // never ran migration 041 lack the rbac_* tables. The store must treat the
        // missing relation (42P01) as "no grants" so PermissionResolver yields
        // NoMatch and AccessPolicyHelpers falls back to the coarse AccessPolicy,
        // instead of surfacing a Postgres 42P01 as an HTTP 500 on enforced query
        // and OGC Features paths.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRoleStoreTests));
        try
        {
            // Deliberately do NOT call EnsureRbacTablesAsync: the schema exists but
            // the RBAC tables do not.
            var store = CreateStore(schema);

            var effective = await store.GetEffectivePermissionsAsync("user-1", ["viewer", "admin"]);
            effective.Should().NotBeNull();
            effective.UserId.Should().Be("user-1");
            effective.Permissions.Should().BeEmpty();

            // Sibling read paths must be resilient too (admin list / get-permissions).
            (await store.ListRolesAsync()).Should().BeEmpty();
            (await store.GetRoleAsync(AdminRoleId)).Should().BeNull();
            (await store.GetPermissionsAsync(ViewerRoleId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresRoleStore CreateStore(string schema)
        => new(new TestConnectionProvider(fixture.DataSource, schema), schemaName: schema);

    private async Task EnsureRbacTablesAsync(string schema)
    {
        // Mirrors migration 041 inside the per-test isolated schema (so the
        // suite runs in parallel) and seeds the built-in roles the store relies
        // on for the built-in-protection and effective-permission assertions.
        await fixture.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "{schema}".rbac_roles (
                role_id       UUID         PRIMARY KEY,
                name          VARCHAR(128) NOT NULL,
                description   TEXT,
                is_built_in   BOOLEAN      NOT NULL DEFAULT FALSE,
                created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_rbac_roles_name_lower_{schema}
                ON "{schema}".rbac_roles (LOWER(name));

            CREATE TABLE IF NOT EXISTS "{schema}".rbac_role_permissions (
                role_id       UUID         NOT NULL REFERENCES "{schema}".rbac_roles(role_id) ON DELETE CASCADE,
                service       VARCHAR(256) NOT NULL,
                layer         VARCHAR(256) NOT NULL,
                operation     VARCHAR(64)  NOT NULL,
                CONSTRAINT rbac_role_permissions_unique_{schema} UNIQUE (role_id, service, layer, operation)
            );

            CREATE TABLE IF NOT EXISTS "{schema}".rbac_role_memberships (
                role_id       UUID         NOT NULL REFERENCES "{schema}".rbac_roles(role_id) ON DELETE CASCADE,
                user_id       VARCHAR(256) NOT NULL,
                created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                CONSTRAINT rbac_role_memberships_pk_{schema} PRIMARY KEY (role_id, user_id)
            );

            INSERT INTO "{schema}".rbac_roles (role_id, name, description, is_built_in)
            VALUES
                ('00000000-0000-0000-0000-000000000001', 'admin',  'Full administrative access', TRUE),
                ('00000000-0000-0000-0000-000000000003', 'editor', 'Read and write', TRUE),
                ('00000000-0000-0000-0000-000000000002', 'viewer', 'Read-only', TRUE)
            ON CONFLICT (role_id) DO NOTHING;

            INSERT INTO "{schema}".rbac_role_permissions (role_id, service, layer, operation)
            VALUES
                ('00000000-0000-0000-0000-000000000001', '*', '*', '*'),
                ('00000000-0000-0000-0000-000000000002', '*', '*', 'query'),
                ('00000000-0000-0000-0000-000000000002', '*', '*', 'metadata')
            ON CONFLICT (role_id, service, layer, operation) DO NOTHING;
            """);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
