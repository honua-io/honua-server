// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeSecurityTriggerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string SecurityMigration = "224_CreateRealtimeSecurityTriggers.sql";
    private const string Role = "00000000-0000-0000-0000-000000000002";
    private static readonly string[] _migrations =
    [
        "041_CreateRbacRoleStore.sql", "065_CreateRlsPolicies.sql", "068_CreateFieldMaskPolicies.sql",
        "106_CreateManagedUsersAndScimGroups.sql", "133_CreatePortalSharing.sql",
        "136_CreateAdminApiKeys.sql", "149_CreateSecurityState.sql"
    ];

    [Fact]
    public void Migration_IsAcceptedAsAdditiveByProductionSafetyClassifier()
    {
        using var stream = typeof(PostgresRealtimeEventLog).Assembly
            .GetManifestResourceStream($"Honua.Server.Migrations.{SecurityMigration}")!;
        using var reader = new StreamReader(stream);
        MigrationSafetyClassifier.Classify(SecurityMigration, reader.ReadToEnd()).Classification
            .Should().Be(MigrationSafetyClassification.Expand);
    }

    [IntegrationTest]
    public async Task PolicyStatements_EmitOncePerStatementAndRollbackWithTheWrite()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("rbac_field_mask_policies")} (policy_id, role, service, layer, attribute)
            VALUES (gen_random_uuid(), '*', 's', '1', 'secret'), (gen_random_uuid(), '*', 's', '2', 'secret');
            """);
        await harness.ExecuteAsync($"""
            BEGIN;
            DELETE FROM {harness.Table("rbac_field_mask_policies")};
            ROLLBACK;
            UPDATE {harness.Table("rbac_field_mask_policies")} SET attribute = 'hidden' WHERE false;
            """);

        (await EventsAsync(harness)).Should().Be("policy-changed:*");
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("rbac_field_mask_policies")}"))
            .Should().Be(2);
    }

    [IntegrationTest]
    public async Task PolicyStoresAndManagedMemberships_UseTheSameSecurityLog()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            UPDATE {harness.Table("rbac_roles")} SET description = 'changed' WHERE role_id = '{Role}';
            UPDATE {harness.Table("rbac_role_permissions")} SET operation = 'read' WHERE role_id = '{Role}' AND operation = 'query';
            INSERT INTO {harness.Table("rbac_rls_policies")} (policy_id, role, service, layer, attribute, claim_type)
            VALUES (gen_random_uuid(), '*', 's', '1', 'owner', 'sub');
            INSERT INTO {harness.Table("portal_item_shares")} (item_id, owner_name) VALUES ('item', 'alice');
            INSERT INTO {harness.Table("managed_users")} (user_id, display_name, provisioning_source)
            VALUES ('alice', 'Alice', 'test');
            INSERT INTO {harness.Table("managed_user_roles")} (user_id, role)
            VALUES ('alice', 'editor'), ('alice', 'viewer');
            DELETE FROM {harness.Table("managed_user_roles")};
            """);

        (await EventsAsync(harness)).Should().Be(
            "policy-changed:*,policy-changed:*,policy-changed:*,policy-changed:*,policy-changed:*,policy-changed:*,policy-changed:*");
    }

    [IntegrationTest]
    public async Task NondefaultSchema_LegacyRbacTablesAppendToConfiguredSecurityLog()
    {
        await using var legacy = await RealtimeLogHarness.CreateAsync(database);
        await using var harness = await CreateHarnessAsync(legacy);
        await harness.ExecuteAsync($"UPDATE {legacy.Table("rbac_roles")} SET description = 'changed' WHERE role_id = '{Role}'");

        (await EventsAsync(harness)).Should().Be("policy-changed:*");
        (await harness.ScalarAsync<string?>($"SELECT to_regclass('{harness.Table("rbac_roles")}')::text"))
            .Should().BeNull();
    }

    [IntegrationTest]
    public async Task ManagedIdentityChanges_InvalidateExternalSubjectsAndCaseVariants()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("managed_users")}
                (user_id, external_id, external_issuer, display_name, provisioning_source)
            VALUES ('Alice', 'external-subject', 'https://issuer.example', 'Alice', 'test');
            INSERT INTO {harness.Table("managed_user_roles")} (user_id, role) VALUES ('Alice', 'editor');
            UPDATE {harness.Table("managed_users")} SET external_id = 'replacement-subject', is_active = false;
            DELETE FROM {harness.Table("managed_user_roles")};
            """);

        (await EventsAsync(harness)).Should().Be(string.Join(',', Enumerable.Repeat("policy-changed:*", 4)));
    }

    [IntegrationTest]
    public async Task MembershipStatements_DeduplicatePrincipalsAndIncludeOldAndNewIdentities()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("rbac_role_memberships")} (role_id, user_id)
            VALUES ('{Role}', 'alice'), ('00000000-0000-0000-0000-000000000001', 'alice');
            UPDATE {harness.Table("rbac_role_memberships")} SET user_id = 'bob' WHERE user_id = 'alice';
            DELETE FROM {harness.Table("rbac_role_memberships")};
            """);

        (await EventsAsync(harness)).Should().Be(
            "principal-changed:alice,principal-changed:alice,principal-changed:bob,principal-changed:bob");
    }

    [IntegrationTest]
    public async Task TokenDeletion_EmitsLiveCredentialHashesWithoutPayloadOrExpirySweepEvents()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("security_tokens")} (kind, token_hash, payload, expires_at)
            VALUES ('AdminSession', decode(repeat('ab', 32), 'hex'), decode('deadcafe', 'hex'), now() + interval '1 hour'),
                   ('PortalToken', decode(repeat('cd', 32), 'hex'), decode('deadcafe', 'hex'), now() - interval '1 hour');
            DELETE FROM {harness.Table("security_tokens")};
            """);

        (await EventsAsync(harness)).Should().Be($"credential-revoked:{string.Concat(Enumerable.Repeat("ab", 32))}");
        (await harness.ScalarAsync<string>($"SELECT payload->>'credentialKind' FROM {harness.Table("realtime_events")}"))
            .Should().Be("AdminSession");
        (await harness.ScalarAsync<bool>($"SELECT bool_and(NOT (payload ? 'payload')) FROM {harness.Table("realtime_events")}"))
            .Should().BeTrue();
    }

    [IntegrationTest]
    public async Task PrincipalRevocationMarker_InsertAndUpsertEmitTheHashedPrincipal()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("security_tokens")} (kind, token_hash, payload, expires_at)
            VALUES ('PortalPrincipalRevocation', decode(repeat('ef', 32), 'hex'), decode('00', 'hex'), now() + interval '1 hour');
            INSERT INTO {harness.Table("security_tokens")} (kind, token_hash, payload, expires_at)
            VALUES ('PortalPrincipalRevocation', decode(repeat('ef', 32), 'hex'), decode('01', 'hex'), now() + interval '2 hours')
            ON CONFLICT (kind, token_hash) DO UPDATE SET payload = excluded.payload, expires_at = excluded.expires_at;
            """);

        var expected = $"credential-revoked:{string.Concat(Enumerable.Repeat("ef", 32))}";
        (await EventsAsync(harness)).Should().Be($"{expected},{expected}");
    }

    [IntegrationTest]
    public async Task AdminKeyUsage_DoesNotRevokeButCredentialChangesAndDeleteDo()
    {
        await using var harness = await CreateHarnessAsync();
        const string id = "aaaaaaaa-0000-0000-0000-000000000001";
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("admin_api_keys")} (id, name, key_prefix, key_hash, created_at, updated_at)
            VALUES ('{id}', 'test', 'prefix', decode('aa', 'hex'), now(), now());
            UPDATE {harness.Table("admin_api_keys")} SET last_used_at = now();
            UPDATE {harness.Table("admin_api_keys")} SET expires_at = now() + interval '1 hour';
            UPDATE {harness.Table("admin_api_keys")} SET revoked_at = now();
            DELETE FROM {harness.Table("admin_api_keys")};
            """);

        (await EventsAsync(harness)).Should().Be(string.Join(',', Enumerable.Repeat($"credential-revoked:{id}", 3)));
    }

    [IntegrationTest]
    public async Task ReapplyMigration_DoesNotDuplicateTriggersAndTruncateInvalidatesPolicies()
    {
        await using var harness = await CreateHarnessAsync();
        await ApplyMigrationAsync(harness, SecurityMigration);
        await harness.ExecuteAsync($"TRUNCATE {harness.Table("portal_item_shares")}");
        (await EventsAsync(harness)).Should().Be("policy-changed:*");
    }

    private async Task<RealtimeLogHarness> CreateHarnessAsync(RealtimeLogHarness? legacy = null)
    {
        var harness = await RealtimeLogHarness.CreateAsync(database);
        foreach (var migration in _migrations)
        {
            await ApplyMigrationAsync(harness, migration, legacy);
        }

        await ApplyMigrationAsync(harness, SecurityMigration, legacy);

        return harness;
    }

    private Task ApplyMigrationAsync(RealtimeLogHarness harness, string name, RealtimeLogHarness? legacy = null)
    {
        using var stream = typeof(PostgresRealtimeEventLog).Assembly.GetManifestResourceStream($"Honua.Server.Migrations.{name}")
            ?? throw new InvalidOperationException($"Migration '{name}' is missing.");
        using var reader = new StreamReader(stream);
        var schema = harness.Table("security_tokens")[..^".security_tokens".Length];
        var legacySchema = (legacy ?? harness).Table("security_tokens")[..^".security_tokens".Length];
        var sql = reader.ReadToEnd().Replace("$HonuaSchema$", schema, StringComparison.Ordinal)
            .Replace("honua.", $"{legacySchema}.", StringComparison.Ordinal)
            .Replace("CREATE SCHEMA IF NOT EXISTS honua;", $"CREATE SCHEMA IF NOT EXISTS {legacySchema};", StringComparison.Ordinal);
        return database.ExecuteDdlUnderLockAsync(sql);
    }

    private static Task<string?> EventsAsync(RealtimeLogHarness harness)
        => harness.ScalarAsync<string>($"""
            SELECT string_agg(kind || ':' || (payload->>'subject'), ',' ORDER BY seq)
            FROM {harness.Table("realtime_events")} WHERE family = 'security'
            """);
}
