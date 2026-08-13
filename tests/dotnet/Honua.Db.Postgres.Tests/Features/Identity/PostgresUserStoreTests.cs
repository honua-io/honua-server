// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Db.Postgres.Features.Identity;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Identity;

/// <summary>
/// Integration tests for <see cref="PostgresUserStore"/> (#3141) using the shared
/// Testcontainers Postgres fixture. Exercises CRUD parity with the in-memory store,
/// stable-identifier resolution (SCIM <c>userName</c> vs OIDC subject / <c>externalId</c>),
/// restart durability, and the cross-replica revocation scenario (two store instances over
/// one database simulating replicas) against an isolated per-test schema mirroring
/// migration 106.
/// </summary>
[Collection("Database")]
public sealed class PostgresUserStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CreateUser_PersistsRecord_ReadableFromSecondStoreInstance()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var storeA = CreateStore(schema);

            var created = await storeA.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "alice@example.com",
                ExternalId = "00u-alice-okta",
                DisplayName = "Alice",
                Email = "alice@example.com",
                Roles = ["editor", "viewer"],
            });

            created.Should().NotBeNull();
            created!.UserId.Should().Be("alice@example.com");
            created.ExternalId.Should().Be("00u-alice-okta");

            // A brand-new store instance (simulating a process restart / second node)
            // must see the persisted record and roles — membership state survives restart.
            var storeB = CreateStore(schema);
            var fetched = await storeB.GetUserAsync("alice@example.com");

            fetched.Should().NotBeNull();
            fetched!.ExternalId.Should().Be("00u-alice-okta");
            fetched.IsActive.Should().BeTrue();
            fetched.Roles.Should().BeEquivalentTo(["editor", "viewer"]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetUser_ByOidcSubject_ResolvesRecordWhoseUserNameDiffers()
    {
        // #3141 finding 2: deferred snapshots capture the OIDC subject while SCIM keys the
        // record by userName. The durable store must resolve either identifier to ONE record.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "alice@example.com",
                ExternalId = "auth0|64f1c2d3e4",
                Roles = ["editor"],
            });

            var bySubject = await store.GetUserAsync("auth0|64f1c2d3e4");
            var byUserName = await store.GetUserAsync("alice@example.com");

            bySubject.Should().NotBeNull();
            byUserName.Should().NotBeNull();
            bySubject!.UserId.Should().Be(byUserName!.UserId);
            bySubject.Roles.Should().BeEquivalentTo(["editor"]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetUserByPrincipalId_CrossColumnCollision_PrefersExternalSubjectOwner()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "subject-owner@example.com",
                ExternalId = "shared-identifier",
                Roles = ["subject-role"],
            });
            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "shared-identifier",
                ExternalId = "different-subject",
                Roles = ["record-id-role"],
            });

            (await store.GetUserAsync("shared-identifier"))!.UserId.Should().Be("shared-identifier");

            var principal = await store.GetUserByPrincipalIdAsync("shared-identifier");
            principal.Should().NotBeNull();
            principal!.UserId.Should().Be("subject-owner@example.com");
            principal.Roles.Should().BeEquivalentTo(["subject-role"]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetUserByPrincipalId_SameSubjectFromDifferentIssuers_IsIssuerScopedAndCaseSensitive()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "issuer-a@example.com",
                ExternalId = "SharedSubject",
                ExternalIssuer = "https://issuer-a.example.com/",
                Roles = ["issuer-a-role"],
            });
            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "issuer-b@example.com",
                ExternalId = "SharedSubject",
                ExternalIssuer = "https://issuer-b.example.com",
                Roles = ["issuer-b-role"],
            });

            (await store.GetUserByPrincipalIdAsync("SharedSubject", "https://issuer-a.example.com/"))!
                .Roles.Should().BeEquivalentTo(["issuer-a-role"]);
            (await store.GetUserByPrincipalIdAsync("SharedSubject", "https://issuer-b.example.com"))!
                .Roles.Should().BeEquivalentTo(["issuer-b-role"]);
            (await store.GetUserAsync("SharedSubject")).Should().BeNull();
            (await store.GetUserByPrincipalIdAsync("sharedsubject", "https://issuer-a.example.com/"))
                .Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DeprovisionUser_OnReplicaA_IsAuthoritativelyInactiveOnReplicaB()
    {
        // #3141 acceptance 1: deactivating a managed user on replica A must
        // deterministically stop deferred work executing on replica B. The deferred lane
        // resolves membership through IUserStore.GetUserAsync; an inactive user (or a
        // resolution miss) fails closed (#3119).
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var replicaA = CreateStore(schema);
            var replicaB = CreateStore(schema);

            await replicaA.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "worker@example.com",
                ExternalId = "sub-worker-1",
                Roles = ["workflow-author"],
            });

            // Replica B sees the active membership first (as it would at capture time).
            var before = await replicaB.GetUserAsync("sub-worker-1");
            before!.IsActive.Should().BeTrue();
            before.Roles.Should().Contain("workflow-author");

            // Replica A deprovisions the user (SCIM deactivate / admin delete).
            (await replicaA.DeprovisionUserAsync("worker@example.com")).Should().BeTrue();

            // Replica B now resolves the identity as inactive with no roles — by userName
            // AND by the OIDC subject captured in the durable snapshot.
            var afterBySubject = await replicaB.GetUserAsync("sub-worker-1");
            afterBySubject.Should().NotBeNull();
            afterBySubject!.IsActive.Should().BeFalse();
            afterBySubject.Roles.Should().BeEmpty();

            var afterByUserName = await replicaB.GetUserAsync("worker@example.com");
            afterByUserName!.IsActive.Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task UpdateUserRoles_RevokedRoleOnReplicaA_IsGoneOnReplicaB()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var replicaA = CreateStore(schema);
            var replicaB = CreateStore(schema);

            await replicaA.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "bob@example.com",
                Roles = ["admin", "editor"],
            });

            var updated = await replicaA.UpdateUserRolesAsync("bob@example.com", ["editor"]);
            updated!.Roles.Should().BeEquivalentTo(["editor"]);

            var onReplicaB = await replicaB.GetUserAsync("bob@example.com");
            onReplicaB!.Roles.Should().BeEquivalentTo(["editor"]);
            onReplicaB.IsActive.Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task CreateUser_DuplicateUserNameOrExternalId_ReturnsNull()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            (await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "carol@example.com",
                ExternalId = "sub-carol",
            })).Should().NotBeNull();

            // Same userName (case-insensitive) conflicts.
            (await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "CAROL@example.com",
            })).Should().BeNull();

            // Same externalId under a different userName conflicts too.
            (await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "carol.alt@example.com",
                ExternalId = "sub-carol",
            })).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SetActive_Deactivate_ClearsRoles_Reactivate_DoesNotRestoreThem()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "dave@example.com",
                Roles = ["editor"],
            });

            var deactivated = await store.SetActiveAsync("dave@example.com", active: false);
            deactivated!.IsActive.Should().BeFalse();
            deactivated.Roles.Should().BeEmpty();

            // In-memory store parity: reactivation restores the account but not the
            // previously revoked roles.
            var reactivated = await store.SetActiveAsync("dave@example.com", active: true);
            reactivated!.IsActive.Should().BeTrue();
            reactivated.Roles.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReplaceUser_OmittedExternalId_PreservesStoredSubject()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "erin@example.com",
                ExternalId = "sub-erin",
                Roles = ["viewer"],
            });

            // An IdP PUT that omits externalId must not orphan deferred snapshots keyed by
            // the stored subject.
            var replaced = await store.ReplaceUserAsync("erin@example.com", new ScimUserProvisioning
            {
                UserName = "erin@example.com",
                DisplayName = "Erin Updated",
                Roles = ["viewer"],
            });

            replaced!.ExternalId.Should().Be("sub-erin");
            replaced.DisplayName.Should().Be("Erin Updated");
            (await store.GetUserAsync("sub-erin")).Should().NotBeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReplaceUser_StalePreservedRoles_UsesLockedStoredProjection()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "role-race@example.com",
                Roles = ["viewer"],
            });

            // Simulate the SCIM endpoint's pre-read, followed by a concurrent admin role
            // update that commits before the PUT acquires the durable user lock.
            var staleRoles = (await store.GetUserAsync("role-race@example.com"))!.Roles;
            await store.UpdateUserRolesAsync("role-race@example.com", ["editor"]);

            var replaced = await store.ReplaceUserAsync("role-race@example.com", new ScimUserProvisioning
            {
                UserName = "role-race@example.com",
                DisplayName = "Profile Updated",
                Roles = staleRoles,
            });

            replaced!.DisplayName.Should().Be("Profile Updated");
            replaced.Roles.Should().BeEquivalentTo(["editor"]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReplaceUser_SetActiveFalse_ClearsRoles()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);
            await store.CreateUserAsync(new ScimUserProvisioning
            {
                UserName = "put-deactivate@example.com",
                Roles = ["editor"],
            });

            var replaced = await store.ReplaceUserAsync("put-deactivate@example.com", new ScimUserProvisioning
            {
                UserName = "put-deactivate@example.com",
                Active = false,
                Roles = ["editor"],
            });

            replaced!.IsActive.Should().BeFalse();
            replaced.Roles.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListUsers_AdminFilter_ByRoleSourceAndActive_WithPagination()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning { UserName = "u1@example.com", Roles = ["editor"] });
            await store.CreateUserAsync(new ScimUserProvisioning { UserName = "u2@example.com", Roles = ["viewer"] });
            await store.CreateUserAsync(new ScimUserProvisioning { UserName = "u3@example.com", Roles = ["editor"], Active = false });

            var byRole = await store.ListUsersAsync(new UserListFilter { Role = "EDITOR" });
            byRole.TotalCount.Should().Be(2);
            byRole.Users.Select(u => u.UserId).Should().BeEquivalentTo(["u1@example.com", "u3@example.com"]);

            var active = await store.ListUsersAsync(new UserListFilter { IsActive = true });
            active.TotalCount.Should().Be(2);

            var scimSource = await store.ListUsersAsync(new UserListFilter { ProvisioningSource = "scim" });
            scimSource.TotalCount.Should().Be(3);

            var page = await store.ListUsersAsync(new UserListFilter { Limit = 1, Offset = 1 });
            page.TotalCount.Should().Be(3);
            page.Users.Should().ContainSingle().Which.UserId.Should().Be("u2@example.com");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ListUsers_ScimQuery_FiltersByUserName_WithOneBasedPaging()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            await EnsureManagedUserTablesAsync(schema);
            var store = CreateStore(schema);

            await store.CreateUserAsync(new ScimUserProvisioning { UserName = "s1@example.com" });
            await store.CreateUserAsync(new ScimUserProvisioning { UserName = "s2@example.com" });
            await store.CreateUserAsync(new ScimUserProvisioning { UserName = "s3@example.com" });

            var filtered = await store.ListUsersAsync(new ScimUserQuery { UserNameEquals = "S2@EXAMPLE.COM" });
            filtered.TotalCount.Should().Be(1);
            filtered.Users.Should().ContainSingle().Which.UserId.Should().Be("s2@example.com");

            var paged = await store.ListUsersAsync(new ScimUserQuery { StartIndex = 2, Count = 1 });
            paged.TotalCount.Should().Be(3);
            paged.Users.Should().ContainSingle().Which.UserId.Should().Be("s2@example.com");

            (await store.FindByUserNameAsync("s3@example.com"))!.UserId.Should().Be("s3@example.com");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Reads_ManagedUserTablesAbsent_ReturnEmpty_DoNotThrow()
    {
        // Fresh/legacy DB where migration 106 has not run: reads must degrade to "no
        // managed users" (honua-server#1341 resilience pattern) instead of surfacing 42P01.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresUserStoreTests));
        try
        {
            // Deliberately do NOT create the managed-user tables.
            var store = CreateStore(schema);

            (await store.GetUserAsync("anyone")).Should().BeNull();
            (await store.FindByUserNameAsync("anyone")).Should().BeNull();
            (await store.ListUsersAsync(new UserListFilter())).TotalCount.Should().Be(0);
            (await store.ListUsersAsync(new ScimUserQuery())).TotalCount.Should().Be(0);
            (await store.DeprovisionUserAsync("anyone")).Should().BeFalse();
            (await store.UpdateUserRolesAsync("anyone", ["editor"])).Should().BeNull();
            (await store.SetActiveAsync("anyone", active: false)).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresUserStore CreateStore(string schema)
        => new(fixture.DataSource, schemaName: schema);

    internal static string ManagedUserTablesDdl(string schema) => $"""
        CREATE TABLE IF NOT EXISTS "{schema}".managed_users (
            user_id             VARCHAR(256) PRIMARY KEY,
            external_id         VARCHAR(256),
            external_issuer     VARCHAR(2048),
            display_name        VARCHAR(512) NOT NULL,
            email               VARCHAR(320),
            provisioning_source VARCHAR(64)  NOT NULL,
            provider_id         UUID,
            is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
            created_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_user_id_lower
            ON "{schema}".managed_users (LOWER(user_id));

        CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_external_identity
            ON "{schema}".managed_users (external_issuer, external_id)
            WHERE external_id IS NOT NULL AND external_issuer IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_users_external_id_legacy
            ON "{schema}".managed_users (external_id)
            WHERE external_id IS NOT NULL AND external_issuer IS NULL;

        CREATE INDEX IF NOT EXISTS ix_managed_users_external_id
            ON "{schema}".managed_users (external_id)
            WHERE external_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS "{schema}".managed_user_roles (
            user_id    VARCHAR(256) NOT NULL REFERENCES "{schema}".managed_users(user_id) ON DELETE CASCADE,
            role       VARCHAR(256) NOT NULL,
            created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_user_roles_user_role_lower
            ON "{schema}".managed_user_roles (user_id, LOWER(role));

        CREATE INDEX IF NOT EXISTS ix_managed_user_roles_role_lower
            ON "{schema}".managed_user_roles (LOWER(role));

        CREATE TABLE IF NOT EXISTS "{schema}".scim_groups (
            group_id     VARCHAR(64)  PRIMARY KEY,
            display_name VARCHAR(256) NOT NULL,
            created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS "{schema}".scim_group_members (
            group_id   VARCHAR(64)  NOT NULL REFERENCES "{schema}".scim_groups(group_id) ON DELETE CASCADE,
            user_id    VARCHAR(256) NOT NULL,
            created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );
        """;

    private async Task EnsureManagedUserTablesAsync(string schema)
    {
        // Mirrors migration 106 (managed-user tables) inside the per-test isolated schema
        // so the suite runs in parallel.
        await fixture.ExecuteAsync(ManagedUserTablesDdl(schema));
    }
}
