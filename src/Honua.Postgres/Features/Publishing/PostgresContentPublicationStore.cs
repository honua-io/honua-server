// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Publishing;

/// <summary>
/// Postgres-backed <see cref="IContentPublicationStore"/>. Version and event rows are
/// append-only (enforced by table rules); the route row is the single mutable pointer.
/// Publish/republish/rollback/policy writes are applied atomically inside a transaction
/// alongside the append-only event so route pointer state and history never diverge.
/// </summary>
internal sealed class PostgresContentPublicationStore : IContentPublicationStore
{
    private const string VersionColumns =
        "version_id, publication_id, revision, kind, route_slug, route_path, title, source_content_id, " +
        "source_package_id, content_hash, content_version_id, source_metadata_revision, source_metadata_etag, " +
        "app_manifest_id, app_bundle_artifact_id, default_view_bbox, policy, dependencies, provenance, " +
        "job_id, operation_id, correlation_id, audit_ref, created_by, created_at";

    private const string RouteColumns =
        "publication_id, route_slug, route_path, kind, active_version_id, active_revision, previous_version_id, " +
        "rollback_target_version_id, lifecycle, policy, generation, etag, updated_by, updated_at, created_at";

    private const string EventColumns =
        "event_seq, event_id, publication_id, operation, version_id, revision, route_slug, actor, correlation_id, detail, created_at";

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _versionsTable;
    private readonly string _routesTable;
    private readonly string _eventsTable;

    public PostgresContentPublicationStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _versionsTable = Infrastructure.SchemaSearchPath.QualifyTable("content_publication_versions", schemaName);
        _routesTable = Infrastructure.SchemaSearchPath.QualifyTable("content_publication_routes", schemaName);
        _eventsTable = Infrastructure.SchemaSearchPath.QualifyTable("content_publication_events", schemaName);
    }

    /// <inheritdoc />
    public async Task<ContentPublicationRouteState?> GetRouteByPublicationIdAsync(string publicationId, CancellationToken cancellationToken = default)
    {
        if (!TryParsePublicationId(publicationId, out var publicationGuid))
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {RouteColumns} FROM {_routesTable} WHERE publication_id = @publication_id", connection);
        command.Parameters.AddWithValue("@publication_id", publicationGuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRoute(reader) : null;
    }

    /// <inheritdoc />
    public async Task<ContentPublicationRouteState?> GetRouteBySlugAsync(string routeSlug, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {RouteColumns} FROM {_routesTable} WHERE route_slug = @route_slug", connection);
        command.Parameters.AddWithValue("@route_slug", routeSlug);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRoute(reader) : null;
    }

    /// <inheritdoc />
    public async Task<ContentPublicationVersion?> GetVersionByIdAsync(string publicationId, string versionId, CancellationToken cancellationToken = default)
    {
        if (!TryParsePublicationId(publicationId, out var publicationGuid)
            || !Guid.TryParse(versionId, out var versionGuid))
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {VersionColumns} FROM {_versionsTable} WHERE publication_id = @publication_id AND version_id = @version_id", connection);
        command.Parameters.AddWithValue("@publication_id", publicationGuid);
        command.Parameters.AddWithValue("@version_id", versionGuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapVersion(reader) : null;
    }

    /// <inheritdoc />
    public async Task<ContentPublicationVersion?> GetVersionByRevisionAsync(string publicationId, long revision, CancellationToken cancellationToken = default)
    {
        if (!TryParsePublicationId(publicationId, out var publicationGuid))
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {VersionColumns} FROM {_versionsTable} WHERE publication_id = @publication_id AND revision = @revision", connection);
        command.Parameters.AddWithValue("@publication_id", publicationGuid);
        command.Parameters.AddWithValue("@revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapVersion(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentPublicationVersion>> ListVersionsAsync(string publicationId, int limit, CancellationToken cancellationToken = default)
    {
        if (!TryParsePublicationId(publicationId, out var publicationGuid))
        {
            return [];
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {VersionColumns} FROM {_versionsTable} WHERE publication_id = @publication_id ORDER BY revision DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("@publication_id", publicationGuid);
        command.Parameters.AddWithValue("@limit", Math.Max(0, limit));
        var results = new List<ContentPublicationVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapVersion(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<long> GetMaxRevisionAsync(string publicationId, CancellationToken cancellationToken = default)
    {
        if (!TryParsePublicationId(publicationId, out var publicationGuid))
        {
            return 0L;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT COALESCE(MAX(revision), 0) FROM {_versionsTable} WHERE publication_id = @publication_id", connection);
        command.Parameters.AddWithValue("@publication_id", publicationGuid);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long revision ? revision : 0L;
    }

    /// <inheritdoc />
    public async Task AppendVersionAndSetRouteAsync(
        ContentPublicationVersion version,
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(routeState);
        ArgumentNullException.ThrowIfNull(auditEvent);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);

            if (expectedEtag is null)
            {
                await InsertRouteAsync(connection, transaction, routeState, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var updated = await UpdateRouteAsync(connection, transaction, routeState, expectedEtag, cancellationToken).ConfigureAwait(false);
                if (updated == 0)
                {
                    throw new ContentPublicationConflictException("Route was modified concurrently (etag mismatch).");
                }
            }

            await InsertEventAsync(connection, transaction, auditEvent, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw MapUniqueViolation(ex, routeState.RouteSlug);
        }
        catch
        {
            await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetRouteAsync(
        ContentPublicationRouteState routeState,
        ContentPublicationEvent auditEvent,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeState);
        ArgumentNullException.ThrowIfNull(auditEvent);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var updated = await UpdateRouteAsync(connection, transaction, routeState, expectedEtag, cancellationToken).ConfigureAwait(false);
            if (updated == 0)
            {
                throw new ContentPublicationConflictException("Route not found or modified concurrently (etag mismatch).");
            }

            await InsertEventAsync(connection, transaction, auditEvent, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ContentPublicationConflictException)
        {
            await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentPublicationEvent>> ListEventsAsync(string publicationId, int limit, CancellationToken cancellationToken = default)
    {
        if (!TryParsePublicationId(publicationId, out var publicationGuid))
        {
            return [];
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {EventColumns} FROM {_eventsTable} WHERE publication_id = @publication_id ORDER BY event_seq DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("@publication_id", publicationGuid);
        command.Parameters.AddWithValue("@limit", Math.Max(0, limit));
        var results = new List<ContentPublicationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapEvent(reader));
        }

        return results;
    }

    private async Task InsertVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ContentPublicationVersion version, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_versionsTable} ({VersionColumns})
            VALUES (@version_id, @publication_id, @revision, @kind, @route_slug, @route_path, @title, @source_content_id,
                    @source_package_id, @content_hash, @content_version_id, @source_metadata_revision, @source_metadata_etag,
                    @app_manifest_id, @app_bundle_artifact_id, @default_view_bbox, @policy, @dependencies, @provenance,
                    @job_id, @operation_id, @correlation_id, @audit_ref, @created_by, @created_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@version_id", Guid.Parse(version.VersionId));
        command.Parameters.AddWithValue("@publication_id", Guid.Parse(version.PublicationId));
        command.Parameters.AddWithValue("@revision", version.Revision);
        command.Parameters.AddWithValue("@kind", KindToDb(version.Kind));
        command.Parameters.AddWithValue("@route_slug", version.RouteSlug);
        command.Parameters.AddWithValue("@route_path", version.RoutePath);
        command.Parameters.AddWithValue("@title", NullableText(version.Title));
        command.Parameters.AddWithValue("@source_content_id", NullableText(version.SourceContentId));
        command.Parameters.AddWithValue("@source_package_id", NullableText(version.SourcePackageId));
        command.Parameters.AddWithValue("@content_hash", NullableText(version.ContentHash));
        command.Parameters.AddWithValue("@content_version_id", NullableText(version.ContentVersionId));
        command.Parameters.AddWithValue("@source_metadata_revision", (object?)version.SourceMetadataRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("@source_metadata_etag", NullableText(version.SourceMetadataEtag));
        command.Parameters.AddWithValue("@app_manifest_id", NullableText(version.AppManifestId));
        command.Parameters.AddWithValue("@app_bundle_artifact_id", NullableText(version.AppBundleArtifactId));
        command.Parameters.Add(JsonbParameter("@default_view_bbox", version.DefaultViewBbox is null
            ? null
            : JsonSerializer.Serialize(version.DefaultViewBbox, ContentPublicationJsonContext.Default.ContentPublicationBbox)));
        command.Parameters.Add(JsonbParameter("@policy",
            JsonSerializer.Serialize(version.Policy, ContentPublicationJsonContext.Default.ContentPublicationPolicy)));
        command.Parameters.Add(JsonbParameter("@dependencies",
            JsonSerializer.Serialize(version.Dependencies.ToArray(), ContentPublicationJsonContext.Default.ContentPublicationDependencyRefArray)));
        command.Parameters.Add(JsonbParameter("@provenance",
            JsonSerializer.Serialize(version.Provenance.ToArray(), ContentPublicationJsonContext.Default.ContentPublicationProvenanceRefArray)));
        command.Parameters.AddWithValue("@job_id", NullableText(version.JobId));
        command.Parameters.AddWithValue("@operation_id", NullableText(version.OperationId));
        command.Parameters.AddWithValue("@correlation_id", NullableText(version.CorrelationId));
        command.Parameters.AddWithValue("@audit_ref", NullableText(version.AuditRef));
        command.Parameters.AddWithValue("@created_by", version.CreatedBy);
        command.Parameters.AddWithValue("@created_at", version.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertRouteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ContentPublicationRouteState route, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_routesTable} ({RouteColumns})
            VALUES (@publication_id, @route_slug, @route_path, @kind, @active_version_id, @active_revision,
                    @previous_version_id, @rollback_target_version_id, @lifecycle, @policy, @generation, @etag,
                    @updated_by, @updated_at, @created_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddRouteCommonParameters(command, route);
        command.Parameters.AddWithValue("@created_at", route.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> UpdateRouteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ContentPublicationRouteState route, string? expectedEtag, CancellationToken cancellationToken)
    {
        var etagPredicate = expectedEtag is null ? string.Empty : " AND etag = @expected_etag";
        var sql = $"""
            UPDATE {_routesTable}
            SET active_version_id = @active_version_id,
                active_revision = @active_revision,
                previous_version_id = @previous_version_id,
                rollback_target_version_id = @rollback_target_version_id,
                lifecycle = @lifecycle,
                policy = @policy,
                generation = @generation,
                etag = @etag,
                updated_by = @updated_by,
                updated_at = @updated_at
            WHERE publication_id = @publication_id{etagPredicate}
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddRouteCommonParameters(command, route);
        if (expectedEtag is not null)
        {
            command.Parameters.AddWithValue("@expected_etag", expectedEtag);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddRouteCommonParameters(NpgsqlCommand command, ContentPublicationRouteState route)
    {
        command.Parameters.AddWithValue("@publication_id", Guid.Parse(route.PublicationId));
        command.Parameters.AddWithValue("@route_slug", route.RouteSlug);
        command.Parameters.AddWithValue("@route_path", route.RoutePath);
        command.Parameters.AddWithValue("@kind", KindToDb(route.Kind));
        command.Parameters.AddWithValue("@active_version_id", Guid.Parse(route.ActiveVersionId));
        command.Parameters.AddWithValue("@active_revision", route.ActiveRevision);
        command.Parameters.AddWithValue("@previous_version_id", NullableGuid(route.PreviousVersionId));
        command.Parameters.AddWithValue("@rollback_target_version_id", NullableGuid(route.RollbackTargetVersionId));
        command.Parameters.AddWithValue("@lifecycle", LifecycleToDb(route.Lifecycle));
        command.Parameters.Add(JsonbParameter("@policy",
            JsonSerializer.Serialize(route.Policy, ContentPublicationJsonContext.Default.ContentPublicationPolicy)));
        command.Parameters.AddWithValue("@generation", route.Generation);
        command.Parameters.AddWithValue("@etag", route.Etag);
        command.Parameters.AddWithValue("@updated_by", route.UpdatedBy);
        command.Parameters.AddWithValue("@updated_at", route.UpdatedAt);
    }

    private async Task InsertEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ContentPublicationEvent auditEvent, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_eventsTable}
                (event_id, publication_id, operation, version_id, revision, route_slug, actor, correlation_id, detail, created_at)
            VALUES
                (@event_id, @publication_id, @operation, @version_id, @revision, @route_slug, @actor, @correlation_id, @detail, @created_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@event_id", Guid.Parse(auditEvent.EventId));
        command.Parameters.AddWithValue("@publication_id", Guid.Parse(auditEvent.PublicationId));
        command.Parameters.AddWithValue("@operation", OperationToDb(auditEvent.Operation));
        command.Parameters.AddWithValue("@version_id", NullableGuid(auditEvent.VersionId));
        command.Parameters.AddWithValue("@revision", (object?)auditEvent.Revision ?? DBNull.Value);
        command.Parameters.AddWithValue("@route_slug", auditEvent.RouteSlug);
        command.Parameters.AddWithValue("@actor", auditEvent.Actor);
        command.Parameters.AddWithValue("@correlation_id", NullableText(auditEvent.CorrelationId));
        command.Parameters.AddWithValue("@detail", NullableText(auditEvent.Detail));
        command.Parameters.AddWithValue("@created_at", auditEvent.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ContentPublicationVersion MapVersion(NpgsqlDataReader reader) => new()
    {
        VersionId = reader.GetGuid(0).ToString("D"),
        PublicationId = reader.GetGuid(1).ToString("D"),
        Revision = reader.GetInt64(2),
        Kind = KindFromDb(reader.GetString(3)),
        RouteSlug = reader.GetString(4),
        RoutePath = reader.GetString(5),
        Title = StringOrNull(reader, 6),
        SourceContentId = StringOrNull(reader, 7),
        SourcePackageId = StringOrNull(reader, 8),
        ContentHash = StringOrNull(reader, 9),
        ContentVersionId = StringOrNull(reader, 10),
        SourceMetadataRevision = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        SourceMetadataEtag = StringOrNull(reader, 12),
        AppManifestId = StringOrNull(reader, 13),
        AppBundleArtifactId = StringOrNull(reader, 14),
        DefaultViewBbox = reader.IsDBNull(15)
            ? null
            : JsonSerializer.Deserialize(reader.GetString(15), ContentPublicationJsonContext.Default.ContentPublicationBbox),
        Policy = JsonSerializer.Deserialize(reader.GetString(16), ContentPublicationJsonContext.Default.ContentPublicationPolicy)
            ?? new ContentPublicationPolicy(),
        Dependencies = JsonSerializer.Deserialize(reader.GetString(17), ContentPublicationJsonContext.Default.ContentPublicationDependencyRefArray)
            ?? [],
        Provenance = JsonSerializer.Deserialize(reader.GetString(18), ContentPublicationJsonContext.Default.ContentPublicationProvenanceRefArray)
            ?? [],
        JobId = StringOrNull(reader, 19),
        OperationId = StringOrNull(reader, 20),
        CorrelationId = StringOrNull(reader, 21),
        AuditRef = StringOrNull(reader, 22),
        CreatedBy = reader.GetString(23),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(24),
    };

    private static ContentPublicationRouteState MapRoute(NpgsqlDataReader reader) => new()
    {
        PublicationId = reader.GetGuid(0).ToString("D"),
        RouteSlug = reader.GetString(1),
        RoutePath = reader.GetString(2),
        Kind = KindFromDb(reader.GetString(3)),
        ActiveVersionId = reader.GetGuid(4).ToString("D"),
        ActiveRevision = reader.GetInt64(5),
        PreviousVersionId = reader.IsDBNull(6) ? null : reader.GetGuid(6).ToString("D"),
        RollbackTargetVersionId = reader.IsDBNull(7) ? null : reader.GetGuid(7).ToString("D"),
        Lifecycle = LifecycleFromDb(reader.GetString(8)),
        Policy = JsonSerializer.Deserialize(reader.GetString(9), ContentPublicationJsonContext.Default.ContentPublicationPolicy)
            ?? new ContentPublicationPolicy(),
        Generation = reader.GetInt64(10),
        Etag = reader.GetString(11),
        UpdatedBy = reader.GetString(12),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(13),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
    };

    private static ContentPublicationEvent MapEvent(NpgsqlDataReader reader) => new()
    {
        Sequence = reader.GetInt64(0),
        EventId = reader.GetGuid(1).ToString("D"),
        PublicationId = reader.GetGuid(2).ToString("D"),
        Operation = OperationFromDb(reader.GetString(3)),
        VersionId = reader.IsDBNull(4) ? null : reader.GetGuid(4).ToString("D"),
        Revision = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        RouteSlug = reader.GetString(6),
        Actor = reader.GetString(7),
        CorrelationId = StringOrNull(reader, 8),
        Detail = StringOrNull(reader, 9),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
    };

    private static ContentPublicationConflictException MapUniqueViolation(PostgresException ex, string routeSlug)
    {
        if (ex.ConstraintName is { } constraint && constraint.Contains("routes_slug", StringComparison.Ordinal))
        {
            return new ContentPublicationConflictException($"Route slug '{routeSlug}' is already claimed by another publication.");
        }

        if (ex.ConstraintName is { } revisionConstraint && revisionConstraint.Contains("revision_unique", StringComparison.Ordinal))
        {
            return new ContentPublicationConflictException("Version revision already exists for this publication.");
        }

        return new ContentPublicationConflictException("Content publication write conflicts with existing state.");
    }

    private static async Task SafeRollbackAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection may already be faulted; the using-scope disposes it.
        }
    }

    private static NpgsqlParameter JsonbParameter(string name, string? json)
        => new(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value };

    private static object NullableText(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static object NullableGuid(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : Guid.Parse(value);

    private static bool TryParsePublicationId(string publicationId, out Guid publicationGuid)
        => Guid.TryParse(publicationId, out publicationGuid);

    private static string? StringOrNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string KindToDb(ContentPublicationKind kind) => kind switch
    {
        ContentPublicationKind.Map => "map",
        ContentPublicationKind.Dashboard => "dashboard",
        ContentPublicationKind.Report => "report",
        ContentPublicationKind.GeneratedApp => "generated-app",
        _ => "map",
    };

    private static ContentPublicationKind KindFromDb(string kind) => kind switch
    {
        "map" => ContentPublicationKind.Map,
        "dashboard" => ContentPublicationKind.Dashboard,
        "report" => ContentPublicationKind.Report,
        "generated-app" => ContentPublicationKind.GeneratedApp,
        _ => ContentPublicationKind.Map,
    };

    private static string LifecycleToDb(ContentPublicationLifecycle lifecycle) => lifecycle switch
    {
        ContentPublicationLifecycle.Active => "active",
        ContentPublicationLifecycle.Suspended => "suspended",
        ContentPublicationLifecycle.Archived => "archived",
        _ => "active",
    };

    private static ContentPublicationLifecycle LifecycleFromDb(string lifecycle) => lifecycle switch
    {
        "active" => ContentPublicationLifecycle.Active,
        "suspended" => ContentPublicationLifecycle.Suspended,
        "archived" => ContentPublicationLifecycle.Archived,
        _ => ContentPublicationLifecycle.Active,
    };

    private static string OperationToDb(ContentPublicationOperation operation) => operation switch
    {
        ContentPublicationOperation.Publish => "publish",
        ContentPublicationOperation.Republish => "republish",
        ContentPublicationOperation.Rollback => "rollback",
        ContentPublicationOperation.PolicyUpdate => "policy-update",
        ContentPublicationOperation.PublicLinkCreate => "public-link-create",
        ContentPublicationOperation.PublicLinkRevoke => "public-link-revoke",
        ContentPublicationOperation.PublicLinkDenied => "public-link-denied",
        ContentPublicationOperation.EmbedDenied => "embed-denied",
        _ => "policy-update",
    };

    private static ContentPublicationOperation OperationFromDb(string operation) => operation switch
    {
        "publish" => ContentPublicationOperation.Publish,
        "republish" => ContentPublicationOperation.Republish,
        "rollback" => ContentPublicationOperation.Rollback,
        "policy-update" => ContentPublicationOperation.PolicyUpdate,
        "public-link-create" => ContentPublicationOperation.PublicLinkCreate,
        "public-link-revoke" => ContentPublicationOperation.PublicLinkRevoke,
        "public-link-denied" => ContentPublicationOperation.PublicLinkDenied,
        "embed-denied" => ContentPublicationOperation.EmbedDenied,
        _ => ContentPublicationOperation.PolicyUpdate,
    };
}
