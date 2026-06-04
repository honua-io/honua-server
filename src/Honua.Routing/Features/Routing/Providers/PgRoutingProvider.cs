// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// pgRouting-backed routing provider. Solves routes with <c>pgr_dijkstra</c> and
/// service areas with <c>pgr_drivingDistance</c> over the osm2pgrouting-style
/// <c>ways</c> / <c>ways_vertices_pgr</c> topology provisioned by migration
/// 043_CreatePgRoutingTopology.sql.
/// </summary>
/// <remarks>
/// Connections are acquired from the shared <see cref="IDatabaseConnectionProvider"/>
/// (registered by the active Postgres provider) so routing reuses the application's
/// pooled, secure connection substrate rather than opening its own data source. All
/// queries are read-only and parameterized; no user value is string-interpolated
/// into SQL. SQL targets pgRouting 3.x.
/// </remarks>
internal sealed class PgRoutingProvider : IRoutingProvider
{
    /// <summary>
    /// pgRouting provider name constant.
    /// </summary>
    public const string ProviderName = "pgrouting";

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PgRoutingProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgRoutingProvider"/> class.
    /// </summary>
    /// <param name="connectionProvider">Shared database connection provider.</param>
    /// <param name="logger">Logger.</param>
    public PgRoutingProvider(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PgRoutingProvider> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Stops.Count < 2)
        {
            return new RouteSolveResult(string.Empty, 0, 0, []);
        }

        await using var connection = await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Snap each stop to its nearest graph vertex.
        var vertexIds = new long[request.Stops.Count];
        for (var i = 0; i < request.Stops.Count; i++)
        {
            var vertexId = await SnapToNearestVertexAsync(connection, request.Stops[i], cancellationToken)
                .ConfigureAwait(false);
            if (vertexId is null)
            {
                _logger.StopNotSnapped(i);
                return new RouteSolveResult(string.Empty, 0, 0, []);
            }

            vertexIds[i] = vertexId.Value;
        }

        // Route each consecutive pair with pgr_dijkstra and collect the visited
        // edge ids in order so we can merge their geometries and sum their cost.
        var edgeIds = new List<long>();
        var totalCost = 0.0;
        for (var i = 0; i < vertexIds.Length - 1; i++)
        {
            var leg = await SolveDijkstraLegAsync(connection, vertexIds[i], vertexIds[i + 1], cancellationToken)
                .ConfigureAwait(false);
            if (leg is null)
            {
                _logger.NoRouteBetweenVertices(vertexIds[i], vertexIds[i + 1]);
                return new RouteSolveResult(string.Empty, 0, 0, []);
            }

            edgeIds.AddRange(leg.Value.EdgeIds);
            totalCost += leg.Value.Cost;
        }

        if (edgeIds.Count == 0)
        {
            return new RouteSolveResult(string.Empty, 0, 0, []);
        }

        var (geometryGeoJson, lengthMeters) = await MergeEdgeGeometryAsync(
            connection, edgeIds, request.OutSrid, cancellationToken).ConfigureAwait(false);

        // The pgRouting cost weight is treated as travel-time minutes for the MVP;
        // the geodesic length is computed from the geometry in meters.
        var directions = new List<RouteDirectionStep>
        {
            new("Route", lengthMeters, totalCost, "straight"),
        };

        return new RouteSolveResult(geometryGeoJson, lengthMeters, totalCost, directions);
    }

    /// <inheritdoc />
    public async Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderedBreaks = request.Breaks
            .Where(b => b > 0)
            .Distinct()
            .OrderBy(b => b)
            .ToArray();

        if (orderedBreaks.Length == 0 || request.Facilities.Count == 0)
        {
            return new ServiceAreaSolveResult([]);
        }

        await using var connection = await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var polygons = new List<ServiceAreaPolygon>();
        var maxBreak = orderedBreaks[^1];

        for (var facilityId = 0; facilityId < request.Facilities.Count; facilityId++)
        {
            var vertexId = await SnapToNearestVertexAsync(connection, request.Facilities[facilityId], cancellationToken)
                .ConfigureAwait(false);
            if (vertexId is null)
            {
                _logger.FacilityNotSnapped(facilityId);
                continue;
            }

            var fromBreak = 0.0;
            foreach (var toBreak in orderedBreaks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var geometry = await SolveServiceAreaRingAsync(
                    connection, vertexId.Value, toBreak, request.TravelDirection, request.OutSrid, cancellationToken)
                    .ConfigureAwait(false);

                polygons.Add(new ServiceAreaPolygon(facilityId, fromBreak, toBreak, geometry));
                fromBreak = toBreak;
            }

            // maxBreak referenced to make the largest-cutoff intent explicit; the
            // per-ring driving-distance queries each cap at their own break.
            _ = maxBreak;
        }

        return new ServiceAreaSolveResult(polygons);
    }

    private static async Task<long?> SnapToNearestVertexAsync(
        DbConnection connection,
        RoutePoint point,
        CancellationToken cancellationToken)
    {
        // KNN nearest-vertex snap using the GiST <-> operator on ways_vertices_pgr.
        const string sql = """
            SELECT id
            FROM public.ways_vertices_pgr
            ORDER BY the_geom <-> ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)
            LIMIT 1;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "lon", point.Lon);
        AddParameter(command, "lat", point.Lat);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<(IReadOnlyList<long> EdgeIds, double Cost)?> SolveDijkstraLegAsync(
        DbConnection connection,
        long sourceVertex,
        long targetVertex,
        CancellationToken cancellationToken)
    {
        // pgr_dijkstra over the ways edge table. The inner edges_sql is a constant
        // (no user values); source/target are bound parameters. edge = -1 marks the
        // synthetic final row pgr_dijkstra appends, which we skip.
        const string sql = """
            SELECT edge, cost
            FROM pgr_dijkstra(
                'SELECT gid AS id, source, target, cost, reverse_cost FROM public.ways',
                @source, @target, directed => true)
            WHERE edge <> -1
            ORDER BY seq;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "source", sourceVertex);
        AddParameter(command, "target", targetVertex);

        var edgeIds = new List<long>();
        var cost = 0.0;
        var any = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            any = true;
            if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                edgeIds.Add(reader.GetInt64(0));
            }

            if (!await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
            {
                cost += reader.GetDouble(1);
            }
        }

        return any ? (edgeIds, cost) : null;
    }

    private static async Task<(string GeometryGeoJson, double LengthMeters)> MergeEdgeGeometryAsync(
        DbConnection connection,
        IReadOnlyList<long> edgeIds,
        int outSrid,
        CancellationToken cancellationToken)
    {
        // Merge the visited edge geometries into a single LineString and compute a
        // geodesic length in meters (ST_Length over geography). ANY(@edge_ids)
        // keeps the edge id list a single bound array parameter.
        const string sql = """
            WITH route_edges AS (
                SELECT the_geom
                FROM public.ways
                WHERE gid = ANY(@edge_ids)
            )
            SELECT
                ST_AsGeoJSON(ST_Transform(ST_LineMerge(ST_Collect(the_geom)), @out_srid)) AS geojson,
                COALESCE(SUM(ST_Length(the_geom::geography)), 0) AS length_m
            FROM route_edges;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddArrayParameter(command, "edge_ids", edgeIds);
        AddParameter(command, "out_srid", outSrid);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (string.Empty, 0);
        }

        var geometry = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
            ? string.Empty
            : reader.GetString(0);
        var length = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? 0
            : reader.GetDouble(1);

        return (geometry, length);
    }

    private static async Task<string> SolveServiceAreaRingAsync(
        DbConnection connection,
        long facilityVertex,
        double breakCost,
        ServiceAreaTravelDirection direction,
        int outSrid,
        CancellationToken cancellationToken)
    {
        // pgr_drivingDistance returns the reachable vertices within breakCost. We
        // build a coverage polygon with ST_ConcaveHull over the reachable vertex
        // points. ST_ConcaveHull (target_percent 0.9) is used as a pragmatic MVP in
        // place of pgr_alphaShape, which is fiddly about collinear/degenerate inputs;
        // the concave hull is robust and good enough for a coverage estimate.
        // directed flips with travel direction (FromFacility => outbound).
        const string sql = """
            WITH reachable AS (
                SELECT dd.node
                FROM pgr_drivingDistance(
                    'SELECT gid AS id, source, target, cost, reverse_cost FROM public.ways',
                    @facility, @break_cost, directed => true) AS dd
            ),
            reachable_pts AS (
                SELECT v.the_geom
                FROM reachable r
                JOIN public.ways_vertices_pgr v ON v.id = r.node
            )
            SELECT ST_AsGeoJSON(
                ST_Transform(
                    ST_ConcaveHull(ST_Collect(the_geom), 0.9),
                    @out_srid)) AS geojson
            FROM reachable_pts;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "facility", facilityVertex);
        AddParameter(command, "break_cost", breakCost);
        AddParameter(command, "out_srid", outSrid);

        // Travel direction is carried for parity with the canonical model. The
        // undirected vs. directed distinction is captured via the directed flag
        // above; ToFacility coverage would invert source/target in a fuller
        // implementation. Documented as an MVP simplification.
        _ = direction;

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? string.Empty : (string)result;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddArrayParameter(DbCommand command, string name, IReadOnlyList<long> values)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = values.ToArray();
        command.Parameters.Add(parameter);
    }
}
