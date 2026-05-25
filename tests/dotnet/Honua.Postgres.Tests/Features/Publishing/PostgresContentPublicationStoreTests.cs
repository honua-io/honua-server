// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Postgres.Features.Publishing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Publishing;

/// <summary>
/// Postgres integration tests for the content publication store: round-trip,
/// route-slug uniqueness, optimistic concurrency, append-only version rows,
/// event ordering, revision reads, and public-link hash JSONB round-trip.
/// </summary>
[Collection("Database")]
public sealed class PostgresContentPublicationStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task RoundTrip_PublishRepublishRollback_PreservesImmutableVersionsAndMovesRoute()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresContentPublicationStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresContentPublicationStore(provider, schema);

            var publicationId = Guid.NewGuid().ToString("D");
            var (v1, route1, publishEvent) = BuildPublish(publicationId, "quarterly", "v1-hash");
            await store.AppendVersionAndSetRouteAsync(v1, route1, publishEvent, expectedEtag: null);

            (await store.GetRouteBySlugAsync("quarterly")).Should().NotBeNull();
            (await store.GetVersionByRevisionAsync(publicationId, 1))!.ContentHash.Should().Be("v1-hash");
            (await store.GetVersionByIdAsync(publicationId, v1.VersionId))!.Revision.Should().Be(1);
            (await store.GetMaxRevisionAsync(publicationId)).Should().Be(1);

            // Republish to revision 2 (etag-guarded), moving the active pointer.
            var (v2, route2, republishEvent) = BuildRepublish(route1, "v2-hash");
            await store.AppendVersionAndSetRouteAsync(v2, route2, republishEvent, expectedEtag: route1.Etag);

            var afterRepublish = await store.GetRouteByPublicationIdAsync(publicationId);
            afterRepublish!.ActiveRevision.Should().Be(2);
            afterRepublish.PreviousVersionId.Should().Be(v1.VersionId);

            // Rollback to revision 1 via SetRoute (no new version).
            var rollbackRoute = afterRepublish with
            {
                ActiveVersionId = v1.VersionId,
                ActiveRevision = 1,
                PreviousVersionId = afterRepublish.ActiveVersionId,
                RollbackTargetVersionId = v1.VersionId,
                Generation = afterRepublish.Generation + 1,
                Etag = ContentPublicationCrypto.NewEtag(),
            };
            await store.SetRouteAsync(rollbackRoute, BuildEvent(publicationId, ContentPublicationOperation.Rollback, v1.VersionId, 1, "quarterly"), expectedEtag: afterRepublish.Etag);

            var afterRollback = await store.GetRouteByPublicationIdAsync(publicationId);
            afterRollback!.ActiveRevision.Should().Be(1);
            afterRollback.RollbackTargetVersionId.Should().Be(v1.VersionId);

            // Two immutable versions remain; events are newest-first.
            (await store.ListVersionsAsync(publicationId, 100)).Should().HaveCount(2);
            var events = await store.ListEventsAsync(publicationId, 100);
            events.Select(e => e.Operation).Should().ContainInOrder(
                ContentPublicationOperation.Rollback,
                ContentPublicationOperation.Republish,
                ContentPublicationOperation.Publish);
            events.Should().BeInDescendingOrder(e => e.Sequence);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AppendVersionAndSetRoute_DuplicateSlug_ThrowsConflict()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresContentPublicationStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var store = new PostgresContentPublicationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var (v1, route1, e1) = BuildPublish(Guid.NewGuid().ToString("D"), "shared-slug", "h1");
            await store.AppendVersionAndSetRouteAsync(v1, route1, e1, expectedEtag: null);

            var (v2, route2, e2) = BuildPublish(Guid.NewGuid().ToString("D"), "shared-slug", "h2");
            var act = () => store.AppendVersionAndSetRouteAsync(v2, route2, e2, expectedEtag: null);

            await act.Should().ThrowAsync<ContentPublicationConflictException>();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SetRoute_WithStaleEtag_ThrowsConflict()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresContentPublicationStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var store = new PostgresContentPublicationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var publicationId = Guid.NewGuid().ToString("D");
            var (v1, route1, e1) = BuildPublish(publicationId, "slug", "h1");
            await store.AppendVersionAndSetRouteAsync(v1, route1, e1, expectedEtag: null);

            var staleUpdate = route1 with { Generation = 2, Etag = ContentPublicationCrypto.NewEtag() };
            var act = () => store.SetRouteAsync(staleUpdate, BuildEvent(publicationId, ContentPublicationOperation.PolicyUpdate, v1.VersionId, 1, "slug"), expectedEtag: "\"stale\"");

            await act.Should().ThrowAsync<ContentPublicationConflictException>();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Versions_AreAppendOnly_UpdateAndDeleteAreNoOps()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresContentPublicationStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var store = new PostgresContentPublicationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var publicationId = Guid.NewGuid().ToString("D");
            var (v1, route1, e1) = BuildPublish(publicationId, "slug", "original-hash");
            await store.AppendVersionAndSetRouteAsync(v1, route1, e1, expectedEtag: null);

            await using var connection = await OpenSchemaConnectionAsync(schema);
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = $"UPDATE \"{schema}\".content_publication_versions SET content_hash = 'tampered';";
                (await update.ExecuteNonQueryAsync()).Should().Be(0, "the append-only rule blocks UPDATE");
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = $"DELETE FROM \"{schema}\".content_publication_versions;";
                (await delete.ExecuteNonQueryAsync()).Should().Be(0, "the append-only rule blocks DELETE");
            }

            (await store.GetVersionByRevisionAsync(publicationId, 1))!.ContentHash.Should().Be("original-hash");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Policy_PublicLinkHash_RoundTripsInJsonbWithoutRawToken()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresContentPublicationStoreTests));
        try
        {
            await EnsureTablesAsync(schema);
            var store = new PostgresContentPublicationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var publicationId = Guid.NewGuid().ToString("D");
            var tokenHash = ContentPublicationCrypto.HashToken("secret-token");
            var policy = new ContentPublicationPolicy
            {
                Visibility = ContentPublicationVisibility.Public,
                PublicLink = new ContentPublicLinkPolicy
                {
                    Enabled = true,
                    Links = [new ContentPublicLink { LinkId = "link-1", TokenHash = tokenHash, TokenHashAlgorithm = "SHA-256", CreatedBy = "u", CreatedAt = DateTimeOffset.UtcNow }],
                },
            };
            var (v1, route1, e1) = BuildPublish(publicationId, "slug", "h1", policy);
            await store.AppendVersionAndSetRouteAsync(v1, route1, e1, expectedEtag: null);

            var route = await store.GetRouteBySlugAsync("slug");
            var link = route!.Policy.PublicLink.Links.Should().ContainSingle().Subject;
            link.TokenHash.Should().Be(tokenHash);
            link.TokenHash.Should().NotContain("secret-token");
            ContentPublicLinkVerifier.TryAuthorize(route.Policy.PublicLink, "link-1", "secret-token", DateTimeOffset.UtcNow).Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReadMethods_MalformedPublicationId_ReturnStableEmptyResults()
    {
        var store = new PostgresContentPublicationStore(new ThrowingConnectionProvider(), schemaName: "unused");

        (await store.GetRouteByPublicationIdAsync("not-a-guid")).Should().BeNull();
        (await store.GetVersionByIdAsync("not-a-guid", Guid.NewGuid().ToString("D"))).Should().BeNull();
        (await store.GetVersionByRevisionAsync("not-a-guid", 1)).Should().BeNull();
        (await store.ListVersionsAsync("not-a-guid", 10)).Should().BeEmpty();
        (await store.GetMaxRevisionAsync("not-a-guid")).Should().Be(0L);
        (await store.ListEventsAsync("not-a-guid", 10)).Should().BeEmpty();
    }

    private static (ContentPublicationVersion, ContentPublicationRouteState, ContentPublicationEvent) BuildPublish(
        string publicationId, string slug, string contentHash, ContentPublicationPolicy? policy = null)
    {
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid().ToString("D");
        var routePath = "/api/v1/published/" + slug;
        var effectivePolicy = policy ?? new ContentPublicationPolicy();
        var version = new ContentPublicationVersion
        {
            PublicationId = publicationId,
            VersionId = versionId,
            Revision = 1,
            Kind = ContentPublicationKind.Map,
            RouteSlug = slug,
            RoutePath = routePath,
            ContentHash = contentHash,
            Policy = effectivePolicy,
            Dependencies = [new ContentPublicationDependencyRef { Kind = ContentPublicationDependencyKind.MapPackage, RefId = "pkg-1" }],
            CreatedBy = "u",
            CreatedAt = now,
        };
        var route = new ContentPublicationRouteState
        {
            PublicationId = publicationId,
            RouteSlug = slug,
            RoutePath = routePath,
            Kind = ContentPublicationKind.Map,
            ActiveVersionId = versionId,
            ActiveRevision = 1,
            Policy = effectivePolicy,
            Generation = 1,
            Etag = ContentPublicationCrypto.NewEtag(),
            UpdatedBy = "u",
            UpdatedAt = now,
            CreatedAt = now,
        };
        return (version, route, BuildEvent(publicationId, ContentPublicationOperation.Publish, versionId, 1, slug));
    }

    private static (ContentPublicationVersion, ContentPublicationRouteState, ContentPublicationEvent) BuildRepublish(
        ContentPublicationRouteState route, string contentHash)
    {
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid().ToString("D");
        var version = new ContentPublicationVersion
        {
            PublicationId = route.PublicationId,
            VersionId = versionId,
            Revision = 2,
            Kind = route.Kind,
            RouteSlug = route.RouteSlug,
            RoutePath = route.RoutePath,
            ContentHash = contentHash,
            Policy = route.Policy,
            CreatedBy = "u",
            CreatedAt = now,
        };
        var updatedRoute = route with
        {
            ActiveVersionId = versionId,
            ActiveRevision = 2,
            PreviousVersionId = route.ActiveVersionId,
            Generation = route.Generation + 1,
            Etag = ContentPublicationCrypto.NewEtag(),
            UpdatedAt = now,
        };
        return (version, updatedRoute, BuildEvent(route.PublicationId, ContentPublicationOperation.Republish, versionId, 2, route.RouteSlug));
    }

    private static ContentPublicationEvent BuildEvent(string publicationId, ContentPublicationOperation operation, string versionId, long revision, string slug)
        => new()
        {
            EventId = Guid.NewGuid().ToString("D"),
            PublicationId = publicationId,
            Operation = operation,
            VersionId = versionId,
            Revision = revision,
            RouteSlug = slug,
            Actor = "u",
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private async Task<NpgsqlConnection> OpenSchemaConnectionAsync(string schema)
    {
        var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SET search_path TO \"{schema}\", public;";
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    private async Task EnsureTablesAsync(string schema)
    {
        await using var connection = await OpenSchemaConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS "{schema}".content_publication_versions (
                version_id               UUID         NOT NULL PRIMARY KEY,
                publication_id           UUID         NOT NULL,
                revision                 BIGINT       NOT NULL,
                kind                     TEXT         NOT NULL,
                route_slug               TEXT         NOT NULL,
                route_path               TEXT         NOT NULL,
                title                    TEXT         NULL,
                source_content_id        TEXT         NULL,
                source_package_id        TEXT         NULL,
                content_hash             TEXT         NULL,
                content_version_id       TEXT         NULL,
                source_metadata_revision BIGINT       NULL,
                source_metadata_etag     TEXT         NULL,
                app_manifest_id          TEXT         NULL,
                app_bundle_artifact_id   TEXT         NULL,
                default_view_bbox        JSONB        NULL,
                policy                   JSONB        NOT NULL,
                dependencies             JSONB        NOT NULL DEFAULT '[]'::jsonb,
                provenance               JSONB        NOT NULL DEFAULT '[]'::jsonb,
                job_id                   TEXT         NULL,
                operation_id             TEXT         NULL,
                correlation_id           TEXT         NULL,
                audit_ref                TEXT         NULL,
                created_by               TEXT         NOT NULL,
                created_at               TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                CONSTRAINT content_publication_versions_revision_unique UNIQUE (publication_id, revision)
            );
            CREATE RULE content_publication_versions_no_update AS ON UPDATE TO "{schema}".content_publication_versions DO INSTEAD NOTHING;
            CREATE RULE content_publication_versions_no_delete AS ON DELETE TO "{schema}".content_publication_versions DO INSTEAD NOTHING;

            CREATE TABLE IF NOT EXISTS "{schema}".content_publication_routes (
                publication_id             UUID         NOT NULL PRIMARY KEY,
                route_slug                 TEXT         NOT NULL,
                route_path                 TEXT         NOT NULL,
                kind                       TEXT         NOT NULL,
                active_version_id          UUID         NOT NULL,
                active_revision            BIGINT       NOT NULL,
                previous_version_id        UUID         NULL,
                rollback_target_version_id UUID         NULL,
                lifecycle                  TEXT         NOT NULL DEFAULT 'active',
                policy                     JSONB        NOT NULL,
                generation                 BIGINT       NOT NULL DEFAULT 1,
                etag                       TEXT         NOT NULL,
                updated_by                 TEXT         NOT NULL,
                updated_at                 TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                created_at                 TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );
            CREATE UNIQUE INDEX idx_content_publication_routes_slug ON "{schema}".content_publication_routes (route_slug);

            CREATE TABLE IF NOT EXISTS "{schema}".content_publication_events (
                event_seq       BIGSERIAL    NOT NULL PRIMARY KEY,
                event_id        UUID         NOT NULL UNIQUE,
                publication_id  UUID         NOT NULL,
                operation       TEXT         NOT NULL,
                version_id      UUID         NULL,
                revision        BIGINT       NULL,
                route_slug      TEXT         NOT NULL,
                actor           TEXT         NOT NULL,
                correlation_id  TEXT         NULL,
                detail          TEXT         NULL,
                created_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
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

    private sealed class ThrowingConnectionProvider : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => throw new InvalidOperationException("Unexpected database access.");

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected database access.");

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected database access.");

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected database access.");

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected database access.");
    }
}
