// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Builds an isolated, generation-scoped pgRouting-shaped shadow edge/vertex topology from
/// a generation's staged content edits (#2716), and computes a lightweight graph-integrity
/// evidence digest (#2718). Every physical table name is server-generated from validated
/// dataset id, generation, and attempt numbers (never raw request text), matching the
/// "isolated shadow topology" safety invariant.
/// </summary>
/// <remarks>
/// Unlike <c>pgr_createTopology</c> (which infers connectivity from geometry-tolerance
/// snapping), this builder derives topology directly from the explicit
/// <see cref="NetworkEdgeEdit.SourceVertexId"/>/<see cref="NetworkEdgeEdit.TargetVertexId"/>
/// stable references #2716 already validated — a more precise source of truth than
/// re-inferring connectivity from geometry once edits exist. Graph-integrity evidence is a
/// portable SQL-only check (edge/vertex counts, self-loop count) rather than
/// <c>pgr_analyzeGraph</c>, so a shadow rebuild does not require the optional
/// <c>pgrouting</c> extension (migration 043 already treats it as optional).
/// </remarks>
public sealed class NetworkTopologyShadowTopologyBuilder
{
    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyShadowTopologyBuilder"/> class.
    /// </summary>
    public NetworkTopologyShadowTopologyBuilder(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <summary>
    /// Computes the deterministic, server-generated shadow table names for one rebuild
    /// attempt. Never derived from raw request text.
    /// </summary>
    public static (string EdgeTable, string VertexTable) ComputeShadowTableNames(string datasetId, long generation, long attempt)
    {
        var slug = SanitizeForIdentifier(datasetId);
        var suffix = $"{slug}_{generation}_{attempt}";
        return ($"honua.topology_shadow_{suffix}_edges", $"honua.topology_shadow_{suffix}_vertices");
    }

    /// <summary>
    /// Materializes the shadow edge/vertex tables for <paramref name="generation"/>'s staged
    /// edits. Idempotent: a re-run of the build stage (e.g. after a worker crash) drops and
    /// recreates the same attempt-scoped tables rather than accumulating duplicates.
    /// </summary>
    public async Task<ShadowTopologyBuildResult> BuildAsync(
        string datasetId,
        long generation,
        long attempt,
        int srid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        var (edgeTable, vertexTable) = ComputeShadowTableNames(datasetId, generation, attempt);
        var parameters = new Dictionary<string, object?>
        {
            ["dataset_id"] = datasetId,
            ["generation"] = generation,
            ["srid"] = srid,
        };

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await session.ExecuteAsync($"DROP TABLE IF EXISTS {edgeTable};", parameters: null, cancellationToken).ConfigureAwait(false);
        await session.ExecuteAsync($"DROP TABLE IF EXISTS {vertexTable};", parameters: null, cancellationToken).ConfigureAwait(false);

        await session.ExecuteAsync(
                $"""
                CREATE TABLE {vertexTable} AS
                SELECT row_number() OVER (ORDER BY vertex_ref) AS id, vertex_ref, the_geom
                FROM (
                    SELECT DISTINCT ON (vertex_ref) vertex_ref, the_geom
                    FROM (
                        SELECT source_vertex_id AS vertex_ref,
                               ST_StartPoint(ST_GeometryN(geometry, 1)) AS the_geom
                        FROM honua.network_topology_edge_edits
                        WHERE dataset_id = @dataset_id AND generation = @generation
                        UNION ALL
                        SELECT target_vertex_id AS vertex_ref,
                               ST_EndPoint(ST_GeometryN(geometry, 1)) AS the_geom
                        FROM honua.network_topology_edge_edits
                        WHERE dataset_id = @dataset_id AND generation = @generation
                    ) AS endpoints
                    ORDER BY vertex_ref
                ) AS distinct_endpoints;
                """,
                parameters,
                cancellationToken)
            .ConfigureAwait(false);
        await session.ExecuteAsync($"ALTER TABLE {vertexTable} ADD PRIMARY KEY (id);", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        await session.ExecuteAsync(
                $"CREATE INDEX ON {vertexTable} USING GIST (the_geom);", parameters: null, cancellationToken)
            .ConfigureAwait(false);

        await session.ExecuteAsync(
                $"""
                CREATE TABLE {edgeTable} AS
                SELECT row_number() OVER (ORDER BY e.edge_id) AS gid,
                       e.edge_id AS edge_ref,
                       sv.id AS source,
                       tv.id AS target,
                       COALESCE((e.attributes ->> 'cost')::double precision, 1.0) AS cost,
                       COALESCE(
                           (e.attributes ->> 'reverse_cost')::double precision,
                           (e.attributes ->> 'cost')::double precision,
                           1.0) AS reverse_cost,
                       e.geometry AS the_geom
                FROM honua.network_topology_edge_edits e
                JOIN {vertexTable} sv ON sv.vertex_ref = e.source_vertex_id
                JOIN {vertexTable} tv ON tv.vertex_ref = e.target_vertex_id
                WHERE e.dataset_id = @dataset_id AND e.generation = @generation;
                """,
                parameters,
                cancellationToken)
            .ConfigureAwait(false);
        await session.ExecuteAsync($"ALTER TABLE {edgeTable} ADD PRIMARY KEY (gid);", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        await session.ExecuteAsync($"CREATE INDEX ON {edgeTable} (source);", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        await session.ExecuteAsync($"CREATE INDEX ON {edgeTable} (target);", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        await session.ExecuteAsync($"CREATE INDEX ON {edgeTable} USING GIST (the_geom);", parameters: null, cancellationToken)
            .ConfigureAwait(false);

        var (edgeCount, vertexCount, selfLoopCount) = await session.QuerySingleOrDefaultAsync(
                $"""
                SELECT
                    (SELECT COUNT(*) FROM {edgeTable})::bigint,
                    (SELECT COUNT(*) FROM {vertexTable})::bigint,
                    (SELECT COUNT(*) FROM {edgeTable} WHERE source = target)::bigint;
                """,
                static row => (row.GetFieldValue<long>(0), row.GetFieldValue<long>(1), row.GetFieldValue<long>(2)),
                parameters: null,
                cancellationToken)
            .ConfigureAwait(false);

        return new ShadowTopologyBuildResult(edgeTable, vertexTable, edgeCount, vertexCount, selfLoopCount);
    }

    /// <summary>
    /// Computes a deterministic, sanitized integrity-evidence digest for a completed shadow
    /// build. Never includes raw geometry, attribute values, or SQL — only counts and
    /// identifiers already safe for audit/telemetry.
    /// </summary>
    public static string ComputeEvidenceDigest(
        string datasetId,
        long generation,
        long sourceRevision,
        long edgeCount,
        long vertexCount)
    {
        var payload = $"{datasetId}|{generation}|{sourceRevision}|{edgeCount}|{vertexCount}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static string SanitizeForIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }

        return builder.ToString();
    }
}

/// <summary>
/// Result of materializing a shadow edge/vertex topology (#2718).
/// </summary>
/// <param name="EdgeTable">Schema-qualified shadow edge table.</param>
/// <param name="VertexTable">Schema-qualified shadow vertex table.</param>
/// <param name="EdgeCount">Number of edges materialized.</param>
/// <param name="VertexCount">Number of distinct vertices materialized.</param>
/// <param name="SelfLoopCount">Number of edges whose source equals its target (integrity warning, not a hard failure).</param>
public sealed record ShadowTopologyBuildResult(
    string EdgeTable,
    string VertexTable,
    long EdgeCount,
    long VertexCount,
    long SelfLoopCount);
