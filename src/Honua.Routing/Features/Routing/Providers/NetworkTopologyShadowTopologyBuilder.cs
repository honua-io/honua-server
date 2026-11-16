// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Routing.Features.Routing.Providers;

public sealed class NetworkTopologyShadowTopologyBuilder
{
    private readonly IDatabaseSessionFactory _sessionFactory;

    public NetworkTopologyShadowTopologyBuilder(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    public static (string EdgeTable, string VertexTable) ComputeShadowTableNames(string datasetId, long generation, long attempt)
    {
        var slug = SanitizeForIdentifier(datasetId);
        var suffix = $"{slug}_{generation}_{attempt}";
        return ($"honua.topology_shadow_{suffix}_edges", $"honua.topology_shadow_{suffix}_vertices");
    }

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

public sealed record ShadowTopologyBuildResult(
    string EdgeTable,
    string VertexTable,
    long EdgeCount,
    long VertexCount,
    long SelfLoopCount);
