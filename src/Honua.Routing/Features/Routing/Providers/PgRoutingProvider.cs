// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
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

    // Edges-SQL for outbound traversal (cost from source to target) and for the
    // reversed graph (source/target swapped) so service-area "to facility" coverage
    // computes who can reach the facility within the cost cutoff. Both are constants
    // — no user value is interpolated.
    private const string OutboundEdgesSql =
        "SELECT gid AS id, source, target, cost, reverse_cost FROM public.ways";
    private const string ReversedEdgesSql =
        "SELECT gid AS id, target AS source, source AS target, cost, reverse_cost FROM public.ways";

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
    public RoutingProviderCapabilities Capabilities { get; } = new(
        SupportsRoute: true,
        SupportsServiceArea: true);

    /// <inheritdoc />
    public async Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = RoutingTelemetry.Source.StartActivity("routing.solveRoute");
        activity?.SetTag("honua.routing.provider", ProviderName);
        activity?.SetTag("honua.routing.stops", request.Stops.Count);
        activity?.SetTag("honua.routing.in_srid", request.InSrid);
        activity?.SetTag("honua.routing.out_srid", request.OutSrid);

        try
        {
            if (request.Stops.Count < 2)
            {
                activity?.SetTag("honua.routing.solved", false);
                return new RouteSolveResult(string.Empty, 0, 0, []);
            }

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Snap each stop to its nearest graph vertex (input transformed from the
            // request SRID to the graph SRID 4326 before the KNN snap).
            var vertexIds = new long[request.Stops.Count];
            for (var i = 0; i < request.Stops.Count; i++)
            {
                var vertexId = await SnapToNearestVertexAsync(
                        connection, request.Stops[i], request.InSrid, cancellationToken)
                    .ConfigureAwait(false);
                if (vertexId is null)
                {
                    _logger.StopNotSnapped(i);
                    activity?.SetTag("honua.routing.solved", false);
                    return new RouteSolveResult(string.Empty, 0, 0, []);
                }

                vertexIds[i] = vertexId.Value;
            }

            // Route each consecutive pair with pgr_dijkstra and collect the visited
            // (node, edge) steps in order so we can thread the geometry in traversal
            // order with correct orientation and sum the cost.
            var steps = new List<RouteStep>();
            var totalCost = 0.0;
            for (var i = 0; i < vertexIds.Length - 1; i++)
            {
                var leg = await SolveDijkstraLegAsync(connection, vertexIds[i], vertexIds[i + 1], cancellationToken)
                    .ConfigureAwait(false);
                if (leg is null)
                {
                    _logger.NoRouteBetweenVertices(vertexIds[i], vertexIds[i + 1]);
                    activity?.SetTag("honua.routing.solved", false);
                    return new RouteSolveResult(string.Empty, 0, 0, []);
                }

                steps.AddRange(leg.Value.Steps);
                totalCost += leg.Value.Cost;
            }

            activity?.SetTag("honua.routing.edges", steps.Count);

            if (steps.Count == 0)
            {
                activity?.SetTag("honua.routing.solved", false);
                return new RouteSolveResult(string.Empty, 0, 0, []);
            }

            var (geometryGeoJson, lengthMeters) = await MergeEdgeGeometryAsync(
                connection, steps, request.OutSrid, cancellationToken).ConfigureAwait(false);

            // The pgRouting cost weight is treated as travel-time minutes for the MVP;
            // the geodesic length is computed from the geometry in meters.
            var directions = new List<RouteDirectionStep>
            {
                new("Route", lengthMeters, totalCost, "straight"),
            };

            var result = new RouteSolveResult(geometryGeoJson, lengthMeters, totalCost, directions);
            activity?.SetTag("honua.routing.solved", result.Solved);
            activity?.SetTag("honua.routing.length_m", lengthMeters);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
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

        using var activity = RoutingTelemetry.Source.StartActivity("routing.solveServiceArea");
        activity?.SetTag("honua.routing.provider", ProviderName);
        activity?.SetTag("honua.routing.facilities", request.Facilities.Count);
        activity?.SetTag("honua.routing.breaks", orderedBreaks.Length);
        activity?.SetTag("honua.routing.travel_direction", request.TravelDirection.ToString());
        activity?.SetTag("honua.routing.in_srid", request.InSrid);
        activity?.SetTag("honua.routing.out_srid", request.OutSrid);

        try
        {
            if (orderedBreaks.Length == 0 || request.Facilities.Count == 0)
            {
                activity?.SetTag("honua.routing.solved", false);
                return new ServiceAreaSolveResult([]);
            }

            await using var connection = await _connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            // ToFacility coverage runs driving-distance over the reversed graph
            // (source/target swapped) so it computes who can reach the facility
            // within the cost cutoff; FromFacility uses the outbound graph.
            var edgesSql = request.TravelDirection == ServiceAreaTravelDirection.ToFacility
                ? ReversedEdgesSql
                : OutboundEdgesSql;

            var polygons = new List<ServiceAreaPolygon>();

            for (var facilityId = 0; facilityId < request.Facilities.Count; facilityId++)
            {
                var vertexId = await SnapToNearestVertexAsync(
                        connection, request.Facilities[facilityId], request.InSrid, cancellationToken)
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
                        connection, vertexId.Value, toBreak, edgesSql, request.OutSrid, cancellationToken)
                        .ConfigureAwait(false);

                    // Skip degenerate rings: pgRouting may reach fewer than three
                    // non-collinear vertices for a tiny break, which cannot form a
                    // polygon. Emitting an empty-rings feature would signal a polygon
                    // that does not exist, so the facility/break pair is omitted and
                    // the absence is surfaced via the adapter's no-solve message.
                    if (string.IsNullOrEmpty(geometry))
                    {
                        fromBreak = toBreak;
                        continue;
                    }

                    polygons.Add(new ServiceAreaPolygon(facilityId, fromBreak, toBreak, geometry));
                    fromBreak = toBreak;
                }
            }

            activity?.SetTag("honua.routing.solved", polygons.Count > 0);
            activity?.SetTag("honua.routing.polygons", polygons.Count);
            return new ServiceAreaSolveResult(polygons);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static async Task<long?> SnapToNearestVertexAsync(
        DbConnection connection,
        RoutePoint point,
        int inSrid,
        CancellationToken cancellationToken)
    {
        // KNN nearest-vertex snap using the GiST <-> operator on ways_vertices_pgr.
        // The probe point is built in the request SRID then transformed to the graph
        // SRID (4326); when @in_srid = 4326 the transform is an identity no-op.
        const string sql = """
            SELECT id
            FROM public.ways_vertices_pgr
            ORDER BY the_geom <-> ST_Transform(
                ST_SetSRID(ST_MakePoint(@lon, @lat), @in_srid), 4326)
            LIMIT 1;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "lon", point.Lon);
        AddParameter(command, "lat", point.Lat);
        AddParameter(command, "in_srid", inSrid);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<(IReadOnlyList<RouteStep> Steps, double Cost)?> SolveDijkstraLegAsync(
        DbConnection connection,
        long sourceVertex,
        long targetVertex,
        CancellationToken cancellationToken)
    {
        // pgr_dijkstra over the ways edge table. The inner edges_sql is a constant
        // (no user values); source/target are bound parameters. edge = -1 marks the
        // synthetic final row pgr_dijkstra appends, which we skip. We capture the
        // per-step node alongside the edge so geometry can be threaded in traversal
        // order with correct orientation.
        const string sql = """
            SELECT node, edge, cost
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

        var steps = new List<RouteStep>();
        var cost = 0.0;
        var any = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            any = true;

            var nodeIsNull = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false);
            var edgeIsNull = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false);
            if (!nodeIsNull && !edgeIsNull)
            {
                steps.Add(new RouteStep(reader.GetInt64(0), reader.GetInt64(1)));
            }

            if (!await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
            {
                cost += reader.GetDouble(2);
            }
        }

        return any ? (steps, cost) : null;
    }

    private static async Task<(string GeometryGeoJson, double LengthMeters)> MergeEdgeGeometryAsync(
        DbConnection connection,
        IReadOnlyList<RouteStep> steps,
        int outSrid,
        CancellationToken cancellationToken)
    {
        // Thread the visited edge geometries into a single ordered LineString,
        // preserving traversal order via WITH ORDINALITY and orienting each edge to
        // start at its step's node (reverse when the edge's stored source is not the
        // step's node). The geodesic length sums each oriented edge's length in
        // meters (ST_Length over geography), which stays correct across legs and
        // revisits. Ordered parallel arrays (@edge_ids, @nodes) bind as two array
        // parameters.
        const string sql = """
            WITH steps AS (
                SELECT e.gid, n.node, e.ord
                FROM unnest(@edge_ids) WITH ORDINALITY AS e(gid, ord)
                JOIN unnest(@nodes)    WITH ORDINALITY AS n(node, ord) USING (ord)
            ),
            oriented AS (
                SELECT
                    s.ord,
                    CASE WHEN w.source = s.node THEN w.the_geom ELSE ST_Reverse(w.the_geom) END AS the_geom
                FROM steps s
                JOIN public.ways w ON w.gid = s.gid
            )
            SELECT
                ST_AsGeoJSON(ST_Transform(ST_MakeLine(the_geom ORDER BY ord), @out_srid)) AS geojson,
                COALESCE(SUM(ST_Length(the_geom::geography)), 0) AS length_m
            FROM oriented;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddArrayParameter(command, "edge_ids", steps.Select(s => s.Edge));
        AddArrayParameter(command, "nodes", steps.Select(s => s.Node));
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
        string edgesSql,
        int outSrid,
        CancellationToken cancellationToken)
    {
        // pgr_drivingDistance returns the reachable vertices within breakCost. We
        // build a coverage polygon with ST_ConcaveHull over the reachable vertex
        // points. ST_ConcaveHull (target_percent 0.9) is used as a pragmatic MVP in
        // place of pgr_alphaShape, which is fiddly about collinear/degenerate inputs;
        // the concave hull is robust and good enough for a coverage estimate. The
        // edges-SQL is selected by travel direction (outbound vs. reversed graph);
        // both forms are constants, so the @edges_sql text is a bound parameter and
        // no user value is interpolated. ST_ConcaveHull yields a non-polygon (point
        // or line) when fewer than three non-collinear vertices are reachable; the
        // type guard returns NULL (mapped to empty) so the caller can skip it.
        const string sql = """
            WITH reachable AS (
                SELECT dd.node
                FROM pgr_drivingDistance(
                    @edges_sql,
                    @facility, @break_cost, directed => true) AS dd
            ),
            reachable_pts AS (
                SELECT v.the_geom
                FROM reachable r
                JOIN public.ways_vertices_pgr v ON v.id = r.node
            ),
            hull AS (
                SELECT ST_ConcaveHull(ST_Collect(the_geom), 0.9) AS geom
                FROM reachable_pts
            )
            SELECT CASE
                WHEN geom IS NULL THEN NULL
                WHEN GeometryType(geom) IN ('POLYGON', 'MULTIPOLYGON')
                    THEN ST_AsGeoJSON(ST_Transform(geom, @out_srid))
                ELSE NULL
            END AS geojson
            FROM hull;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "edges_sql", edgesSql);
        AddParameter(command, "facility", facilityVertex);
        AddParameter(command, "break_cost", breakCost);
        AddParameter(command, "out_srid", outSrid);

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

    private static void AddArrayParameter(DbCommand command, string name, IEnumerable<long> values)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = values.ToArray();
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// One step of a Dijkstra leg: the node visited and the edge traversed from it.
    /// Carrying the node lets the geometry merge orient each edge to start at the
    /// step's node, preserving traversal order across legs and revisits.
    /// </summary>
    private readonly record struct RouteStep(long Node, long Edge);
}
