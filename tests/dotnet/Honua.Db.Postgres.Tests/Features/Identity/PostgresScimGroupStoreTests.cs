// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Db.Postgres.Features.Identity;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Identity;

/// <summary>
/// Integration tests for <see cref="PostgresScimGroupStore"/> (#3141, SCIM groups #510)
/// using the shared Testcontainers Postgres fixture. Exercises group CRUD parity with the
/// in-memory store and the group→role sync onto the durable managed-user record set,
/// against an isolated per-test schema mirroring migration 106.
/// </summary>
[Collection("Database")]
public sealed class PostgresScimGroupStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CreateGroup_GrantsMappedRoleToProvisionedMembers_KeepsUnknownMembers()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (users, groups) = CreateStores(schema);

            await users.CreateUserAsync(new ScimUserProvisioning { UserName = "alice@example.com" });

            var group = await groups.CreateGroupAsync(new ScimGroupProvisioning
            {
                DisplayName = "gis-editors",
                MemberUserIds = ["alice@example.com", "not-provisioned@example.com"],
            });

            group.Should().NotBeNull();
            group!.MemberUserIds.Should().BeEquivalentTo(["alice@example.com", "not-provisioned@example.com"]);

            // The provisioned member received the mapped role; the unknown member stays in
            // the group (no FK) and the role sync was a no-op for it.
            (await users.GetUserAsync("alice@example.com"))!.Roles.Should().Contain("gis-editors");

            // Duplicate display name is a SCIM uniqueness conflict.
            (await groups.CreateGroupAsync(new ScimGroupProvisioning { DisplayName = "GIS-EDITORS" }))
                .Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CreateAndReactivateUser_ReconcilesEarlierGroupMembership()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (users, groups) = CreateStores(schema);

            await groups.CreateGroupAsync(new ScimGroupProvisioning
            {
                DisplayName = "preprovisioned-role",
                MemberUserIds = ["later@example.com"],
            });

            var created = await users.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "later@example.com",
            });
            created!.Roles.Should().Contain("preprovisioned-role");

            (await users.DeleteUserAsync("later@example.com")).Should().BeTrue();
            (await users.SetActiveAsync("later@example.com", active: true))!
                .Roles.Should().Contain("preprovisioned-role");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReplaceUser_Reactivation_ReconcilesRetainedGroupMembership()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (users, groups) = CreateStores(schema);

            await users.CreateUserAsync(new ScimUserProvisioning { UserName = "put-reactivate@example.com" });
            await groups.CreateGroupAsync(new ScimGroupProvisioning
            {
                DisplayName = "retained-role",
                MemberUserIds = ["put-reactivate@example.com"],
            });
            (await users.DeleteUserAsync("put-reactivate@example.com")).Should().BeTrue();

            var replaced = await users.ReplaceUserAsync("put-reactivate@example.com", new ScimUserProvisioning
            {
                UserName = "put-reactivate@example.com",
                Active = true,
            });

            replaced!.IsActive.Should().BeTrue();
            replaced.Roles.Should().Contain("retained-role");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task UpdateMembers_AddAndRemove_SyncsRolesOnDurableRecords()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (users, groups) = CreateStores(schema);

            await users.CreateUserAsync(new ScimUserProvisioning { UserName = "bob@example.com" });
            await users.CreateUserAsync(new ScimUserProvisioning { UserName = "carol@example.com" });

            var group = await groups.CreateGroupAsync(new ScimGroupProvisioning
            {
                DisplayName = "surveyors",
                MemberUserIds = ["bob@example.com"],
            });

            var updated = await groups.UpdateMembersAsync(group!.GroupId, new ScimGroupMemberChange
            {
                Add = ["carol@example.com"],
                Remove = ["bob@example.com"],
            });

            updated!.MemberUserIds.Should().BeEquivalentTo(["carol@example.com"]);
            (await users.GetUserAsync("bob@example.com"))!.Roles.Should().NotContain("surveyors");
            (await users.GetUserAsync("carol@example.com"))!.Roles.Should().Contain("surveyors");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReplaceGroup_Rename_RemapsRoleOnAllMembers()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (users, groups) = CreateStores(schema);

            await users.CreateUserAsync(new ScimUserProvisioning { UserName = "dave@example.com" });

            var group = await groups.CreateGroupAsync(new ScimGroupProvisioning
            {
                DisplayName = "old-role",
                MemberUserIds = ["dave@example.com"],
            });

            var replaced = await groups.ReplaceGroupAsync(group!.GroupId, new ScimGroupProvisioning
            {
                DisplayName = "new-role",
                MemberUserIds = ["dave@example.com"],
            });

            replaced!.DisplayName.Should().Be("new-role");

            var dave = await users.GetUserAsync("dave@example.com");
            dave!.Roles.Should().Contain("new-role");
            dave.Roles.Should().NotContain("old-role");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DeleteGroup_RevokesMappedRole_FromAllMembers_AcrossStoreInstances()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (users, groups) = CreateStores(schema);

            await users.CreateUserAsync(new ScimUserProvisioning { UserName = "erin@example.com" });
            var group = await groups.CreateGroupAsync(new ScimGroupProvisioning
            {
                DisplayName = "temp-role",
                MemberUserIds = ["erin@example.com"],
            });

            // Delete through a SECOND store instance (another replica) — the role
            // revocation must land on the shared record set.
            var groupsReplicaB = new PostgresScimGroupStore(fixture.DataSource, schemaName: schema);
            (await groupsReplicaB.DeleteGroupAsync(group!.GroupId)).Should().BeTrue();

            (await groups.GetGroupAsync(group.GroupId)).Should().BeNull();
            (await users.GetUserAsync("erin@example.com"))!.Roles.Should().NotContain("temp-role");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListGroups_FiltersByDisplayName_WithOneBasedPaging()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var (_, groups) = CreateStores(schema);

            await groups.CreateGroupAsync(new ScimGroupProvisioning { DisplayName = "alpha" });
            await groups.CreateGroupAsync(new ScimGroupProvisioning { DisplayName = "beta" });
            await groups.CreateGroupAsync(new ScimGroupProvisioning { DisplayName = "gamma" });

            var filtered = await groups.ListGroupsAsync(new ScimGroupQuery { DisplayNameEquals = "BETA" });
            filtered.TotalCount.Should().Be(1);
            filtered.Groups.Should().ContainSingle().Which.DisplayName.Should().Be("beta");

            var paged = await groups.ListGroupsAsync(new ScimGroupQuery { StartIndex = 2, Count = 1 });
            paged.TotalCount.Should().Be(3);
            paged.Groups.Should().ContainSingle().Which.DisplayName.Should().Be("beta");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Reads_ScimGroupTablesAbsent_ReturnEmpty_DoNotThrow()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresScimGroupStoreTests));
        try
        {
            // Deliberately do NOT create the SCIM group tables.
            var store = new PostgresScimGroupStore(fixture.DataSource, schemaName: schema);

            (await store.GetGroupAsync("missing")).Should().BeNull();
            (await store.ListGroupsAsync(new ScimGroupQuery())).TotalCount.Should().Be(0);
            (await store.DeleteGroupAsync("missing")).Should().BeFalse();
            (await store.UpdateMembersAsync("missing", new ScimGroupMemberChange())).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private (PostgresUserStore Users, PostgresScimGroupStore Groups) CreateStores(string schema)
        => (new PostgresUserStore(fixture.DataSource, schemaName: schema),
            new PostgresScimGroupStore(fixture.DataSource, schemaName: schema));

    private async Task EnsureTablesAsync(string schema)
    {
        // Mirrors migration 106 (managed-user + SCIM group tables) inside the per-test
        // isolated schema so the suite runs in parallel.
        await fixture.ExecuteAsync($"""
            {PostgresUserStoreTests.ManagedUserTablesDdl(schema)}

            CREATE TABLE IF NOT EXISTS "{schema}".scim_groups (
                group_id     VARCHAR(64)  PRIMARY KEY,
                display_name VARCHAR(256) NOT NULL,
                created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_scim_groups_display_name_lower
                ON "{schema}".scim_groups (LOWER(display_name));

            CREATE TABLE IF NOT EXISTS "{schema}".scim_group_members (
                group_id   VARCHAR(64)  NOT NULL REFERENCES "{schema}".scim_groups(group_id) ON DELETE CASCADE,
                user_id    VARCHAR(256) NOT NULL,
                created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_scim_group_members_group_user_lower
                ON "{schema}".scim_group_members (group_id, LOWER(user_id));

            CREATE INDEX IF NOT EXISTS ix_scim_group_members_user_lower
                ON "{schema}".scim_group_members (LOWER(user_id));
            """);
    }
}
