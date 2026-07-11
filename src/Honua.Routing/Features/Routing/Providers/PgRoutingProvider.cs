// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// pgRouting-backed routing provider. Solves routes with <c>pgr_dijkstra</c> and
/// service areas with <c>pgr_drivingDistance</c> over the osm2pgrouting-style
/// <c>ways</c> / <c>ways_vertices_pgr</c> topology provisioned by migration
/// 043_CreatePgRoutingTopology.sql.
/// </summary>
/// <remarks>
/// Sessions are acquired from the shared <see cref="IDatabaseSessionFactory"/>
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

    private readonly IDatabaseSessionFactory _sessionFactory;
    private readonly ILogger<PgRoutingProvider> _logger;
    private readonly INetworkDatasetResolver _datasetResolver;
    private readonly string _networkDatasetId;

    // Declared physical unit of the topology's cost/reverse_cost weights. All
    // time conversions (route time outputs, isochrone @break_cost, and the
    // closest-facility / OD-cost-matrix cutoffs) go through
    // RoutingCostUnitConverter against this unit so the cost-unit contract is
    // applied in exactly one place.
    private readonly RoutingCostUnit _costUnit;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgRoutingProvider"/> class
    /// bound to the built-in default network dataset (the existing
    /// <c>public.ways</c> topology). Retained for direct construction (tests) that
    /// do not exercise multi-dataset resolution.
    /// </summary>
    /// <param name="sessionFactory">Shared database session factory.</param>
    /// <param name="logger">Logger.</param>
    public PgRoutingProvider(
        IDatabaseSessionFactory sessionFactory,
        ILogger<PgRoutingProvider> logger)
        : this(
            sessionFactory,
            logger,
            new DefaultNetworkDatasetResolver(),
            NetworkDataset.DefaultId,
            RoutingCostUnit.Minutes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PgRoutingProvider"/> class
    /// resolving its edge/vertex tables from the configured network dataset
    /// (Phase 0 of #1882).
    /// </summary>
    /// <param name="sessionFactory">Shared database session factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="datasetResolver">Network-dataset resolver.</param>
    /// <param name="options">Bound routing configuration (selects the dataset id).</param>
    public PgRoutingProvider(
        IDatabaseSessionFactory sessionFactory,
        ILogger<PgRoutingProvider> logger,
        INetworkDatasetResolver datasetResolver,
        IOptions<RoutingConfiguration> options)
        : this(
            sessionFactory,
            logger,
            datasetResolver,
            (options ?? throw new ArgumentNullException(nameof(options))).Value.NetworkDatasetId,
            options.Value.CostUnit)
    {
    }

    private PgRoutingProvider(
        IDatabaseSessionFactory sessionFactory,
        ILogger<PgRoutingProvider> logger,
        INetworkDatasetResolver datasetResolver,
        string networkDatasetId,
        RoutingCostUnit costUnit)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _datasetResolver = datasetResolver ?? throw new ArgumentNullException(nameof(datasetResolver));
        _networkDatasetId = string.IsNullOrWhiteSpace(networkDatasetId)
            ? NetworkDataset.DefaultId
            : networkDatasetId;
        _costUnit = costUnit;
    }

    /// <summary>
    /// Resolves the active network dataset, throwing a clear error when the
    /// configured id is not registered. The dataset's edge/vertex table names are
    /// threaded into the solve SQL.
    /// </summary>
    private async Task<NetworkDataset> ResolveDatasetAsync(CancellationToken cancellationToken)
    {
        var dataset = await _datasetResolver.ResolveAsync(_networkDatasetId, cancellationToken).ConfigureAwait(false);
        return dataset ?? throw new InvalidOperationException(
            $"Network dataset '{_networkDatasetId}' is not registered.");
    }

    // Edges-SQL builders for outbound traversal (cost from source to target) and for
    // the reversed graph (source/target swapped) so service-area "to facility"
    // coverage computes who can reach the facility within the cost cutoff. The edge
    // table name comes from the resolved NetworkDataset; it is a validated
    // schema-qualified identifier (the registry rejects non-conforming names and the
    // built-in default is a trusted constant), so it is safe to embed in the SQL text
    // that pgRouting re-parses (which cannot reference bound parameters). No user
    // value is interpolated.
    private static string OutboundEdgesSql(NetworkDataset dataset, RoutingTravelProfile profile)
        => $"SELECT gid AS id, source, target, {profile.ForwardCostColumn} AS cost, " +
           $"{profile.ReverseCostColumn} AS reverse_cost FROM {dataset.EdgeTable}";

    private static string ReversedEdgesSql(NetworkDataset dataset, RoutingTravelProfile profile)
        => $"SELECT gid AS id, target AS source, source AS target, " +
           $"{profile.ReverseCostColumn} AS cost, {profile.ForwardCostColumn} AS reverse_cost " +
           $"FROM {dataset.EdgeTable}";

    private static string RouteBaseEdgesSqlFor(NetworkDataset dataset, RoutingTravelProfile profile)
        => OutboundEdgesSql(dataset, profile);

    private static RoutingTravelProfile ResolveTravelProfile(NetworkDataset dataset, string? requestedProfile)
    {
        var name = string.IsNullOrWhiteSpace(requestedProfile) ? DrivingTravelMode : requestedProfile;
        var profile = dataset.TravelProfiles.FirstOrDefault(profile =>
                   string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"Travel profile '{name}' is not backed by network dataset '{dataset.Id}'.");
        if (!NetworkDatasetValidation.IsValidTravelProfileName(profile.Name) ||
            !NetworkDatasetValidation.IsValidColumnIdentifier(profile.ForwardCostColumn) ||
            !NetworkDatasetValidation.IsValidColumnIdentifier(profile.ReverseCostColumn))
        {
            throw new InvalidOperationException(
                $"Travel profile '{profile.Name}' contains an unsafe impedance column identifier.");
        }

        return profile;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <summary>
    /// The single travel mode pgRouting can route: the topology stores one
    /// <c>cost</c>/<c>reverse_cost</c> weight pair, so there is no per-mode
    /// impedance. The MVP weight is documented as a driving cost, so the provider
    /// advertises <c>driving</c> only. Walking/cycling/truck require additional
    /// per-mode cost columns (deferred — see ADR-0050 and the parity doc).
    /// </summary>
    public const string DrivingTravelMode = "driving";

    /// <inheritdoc />
    public RoutingProviderCapabilities Capabilities { get; } = new(
        SupportsRoute: true,
        SupportsServiceArea: true,
        SupportsClosestFacility: true,
        SupportsOdCostMatrix: true,
        SupportsLocationAllocation: true)
    {
        SupportsOdStraightLines = true,

        // pgRouting honours all three barrier families by excluding the graph
        // edges each barrier geometry interacts with (see BuildBlockedEdgeFilter).
        SupportedBarrierKinds =
        [
            RouteBarrierKind.Point,
            RouteBarrierKind.Line,
            RouteBarrierKind.Polygon,
        ],

        // Single stored cost weight => driving only. The request surface accepts
        // and validates travelMode, but only this mode is genuinely routable.
        SupportedTravelModes = [DrivingTravelMode],

        // The two cost-matrix problem types implemented for #1874. Other Esri
        // problem types are gated with a 400 by the adapter.
        SupportedLocationAllocationProblemTypes =
        [
            LocationAllocationProblemType.MinimizeImpedance,
            LocationAllocationProblemType.MaximizeCoverage,
        ],
    };

    /// <inheritdoc />
    public async Task<RoutingProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var dataset = await ResolveDatasetAsync(cancellationToken).ConfigureAwait(false);
        return Capabilities with
        {
            SupportedTravelModes = dataset.TravelProfiles.Select(profile => profile.Name).ToArray(),
        };
    }

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

            await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var dataset = await ResolveDatasetAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.dataset", dataset.Id);
            var profile = ResolveTravelProfile(dataset, request.TravelMode ?? request.TravelProfile);
            activity?.SetTag("honua.routing.travel_profile", profile.Name);

            // Resolve barrier-restricted edges first so the same exclusion set
            // applies to every leg. Empty when no barriers are supplied.
            var blockedEdges = await ResolveBlockedEdgesAsync(
                session, dataset, request.Barriers, request.InSrid, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.barriers", request.Barriers.Count);
            activity?.SetTag("honua.routing.blocked_edges", blockedEdges.Count);
            var edgesSql = BuildRouteEdgesSql(dataset, profile, blockedEdges);

            // Snap each stop to its nearest graph vertex (input transformed from the
            // request SRID to the graph SRID 4326 before the KNN snap).
            var vertexIds = new long[request.Stops.Count];
            for (var i = 0; i < request.Stops.Count; i++)
            {
                var vertexId = await SnapToNearestVertexAsync(
                        session, dataset, request.Stops[i], request.InSrid, cancellationToken)
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
                var leg = await SolveDijkstraLegAsync(session, edgesSql, vertexIds[i], vertexIds[i + 1], cancellationToken)
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
                session, dataset, steps, request.OutSrid, cancellationToken).ConfigureAwait(false);

            // The raw pgRouting cost weight is converted to travel-time minutes
            // through the declared cost-unit contract (see RoutingCostUnitConverter /
            // RoutingConfiguration.CostUnit); the geodesic length is computed from the
            // geometry in meters.
            var totalTimeMinutes = RoutingCostUnitConverter.CostToMinutes(totalCost, _costUnit);
            var directions = new List<RouteDirectionStep>
            {
                new("Route", lengthMeters, totalTimeMinutes, "straight"),
            };

            var result = new RouteSolveResult(geometryGeoJson, lengthMeters, totalTimeMinutes, directions);
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

            await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var dataset = await ResolveDatasetAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.dataset", dataset.Id);
            var profile = ResolveTravelProfile(dataset, request.TravelMode);
            activity?.SetTag("honua.routing.travel_profile", profile.Name);

            // Resolve barrier-restricted edges once and exclude them from the
            // reachability graph for every facility/break.
            var blockedEdges = await ResolveBlockedEdgesAsync(
                session, dataset, request.Barriers, request.InSrid, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.barriers", request.Barriers.Count);
            activity?.SetTag("honua.routing.blocked_edges", blockedEdges.Count);

            // ToFacility coverage runs driving-distance over the reversed graph
            // (source/target swapped) so it computes who can reach the facility
            // within the cost cutoff; FromFacility uses the outbound graph.
            var baseEdgesSql = request.TravelDirection == ServiceAreaTravelDirection.ToFacility
                ? ReversedEdgesSql(dataset, profile)
                : OutboundEdgesSql(dataset, profile);
            var edgesSql = ApplyBlockedEdgeFilter(baseEdgesSql, blockedEdges);

            var polygons = new List<ServiceAreaPolygon>();

            for (var facilityId = 0; facilityId < request.Facilities.Count; facilityId++)
            {
                var vertexId = await SnapToNearestVertexAsync(
                        session, dataset, request.Facilities[facilityId], request.InSrid, cancellationToken)
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

                    // Breaks arrive in minutes (Esri defaultBreaks); convert to the
                    // raw cost unit before passing as the pgr_drivingDistance cutoff.
                    // The emitted polygon keeps the request-minute break values.
                    var breakCost = RoutingCostUnitConverter.MinutesToCost(toBreak, _costUnit);
                    var geometry = await SolveServiceAreaRingAsync(
                        session, dataset, vertexId.Value, breakCost, edgesSql, request.OutSrid, cancellationToken)
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

    /// <inheritdoc />
    public async Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(
        ClosestFacilitySolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = RoutingTelemetry.Source.StartActivity("routing.solveClosestFacility");
        activity?.SetTag("honua.routing.provider", ProviderName);
        activity?.SetTag("honua.routing.incidents", request.Incidents.Count);
        activity?.SetTag("honua.routing.facilities", request.Facilities.Count);

        try
        {
            var targetCount = Math.Max(1, request.DefaultTargetFacilityCount);
            if (request.Incidents.Count == 0 || request.Facilities.Count == 0)
            {
                activity?.SetTag("honua.routing.solved", false);
                return new ClosestFacilitySolveResult([]);
            }

            await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var dataset = await ResolveDatasetAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.dataset", dataset.Id);
            var profile = ResolveTravelProfile(dataset, request.TravelMode);
            activity?.SetTag("honua.routing.travel_profile", profile.Name);

            var blockedEdges = await ResolveBlockedEdgesAsync(
                session, dataset, request.Barriers, request.InSrid, cancellationToken).ConfigureAwait(false);

            // ToFacility ranks by the cost incident -> facility over the outbound
            // graph; FromFacility ranks by facility -> incident, which is the cost
            // incident -> facility over the reversed graph (so the same one-to-many
            // probe from the incident vertex applies).
            var baseEdgesSql = request.Direction == ClosestFacilityTravelDirection.FromFacility
                ? ReversedEdgesSql(dataset, profile)
                : OutboundEdgesSql(dataset, profile);
            var edgesSql = ApplyBlockedEdgeFilter(baseEdgesSql, blockedEdges);

            // Snap facilities once; reused across incidents.
            var facilityVertices = await SnapAllAsync(
                session, dataset, request.Facilities, request.InSrid, cancellationToken).ConfigureAwait(false);

            var routes = new List<ClosestFacilityRoute>();
            for (var incidentId = 0; incidentId < request.Incidents.Count; incidentId++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var incidentVertex = await SnapToNearestVertexAsync(
                    session, dataset, request.Incidents[incidentId], request.InSrid, cancellationToken)
                    .ConfigureAwait(false);
                if (incidentVertex is null)
                {
                    continue;
                }

                // One-to-many cost from the incident vertex to all facility vertices.
                var destVids = facilityVertices
                    .Where(f => f.VertexId is not null)
                    .Select(f => f.VertexId!.Value)
                    .Distinct()
                    .ToArray();
                if (destVids.Length == 0)
                {
                    continue;
                }

                var costs = await OneToManyCostAsync(
                    session, edgesSql, incidentVertex.Value, destVids, cancellationToken).ConfigureAwait(false);

                // The cutoff arrives in minutes; convert it to the raw cost unit so it
                // compares against the pgRouting aggregate costs (the same cost-unit
                // contract used for the emitted route time below).
                var cutoffCost = request.Cutoff is { } cutoffMinutes
                    ? RoutingCostUnitConverter.MinutesToCost(cutoffMinutes, _costUnit)
                    : (double?)null;

                // Rank facilities by cost. Each facility maps to its snapped vertex;
                // multiple facilities may share a vertex, so resolve cost per facility.
                var ranked = facilityVertices
                    .Select((f, idx) => (FacilityId: idx, f.VertexId))
                    .Where(x => x.VertexId is not null && costs.TryGetValue(x.VertexId!.Value, out _))
                    .Select(x => (x.FacilityId, Cost: costs[x.VertexId!.Value]))
                    .Where(x => cutoffCost is not { } cutoff || x.Cost <= cutoff)
                    .OrderBy(x => x.Cost)
                    .Take(targetCount)
                    .ToList();

                var rank = 1;
                foreach (var (facilityId, cost) in ranked)
                {
                    // Materialize the route geometry for the chosen pair via a single
                    // pgr_dijkstra leg, then merge into a LineString.
                    var leg = await SolveDijkstraLegAsync(
                        session, edgesSql, incidentVertex.Value, facilityVertices[facilityId].VertexId!.Value, cancellationToken)
                        .ConfigureAwait(false);

                    var geometry = string.Empty;
                    var length = 0.0;
                    if (leg is { } legValue && legValue.Steps.Count > 0)
                    {
                        (geometry, length) = await MergeEdgeGeometryAsync(
                            session, dataset, legValue.Steps, request.OutSrid, cancellationToken).ConfigureAwait(false);
                    }

                    // Convert the raw cost impedance to travel-time minutes for the
                    // route time and its single summary direction step.
                    var costMinutes = RoutingCostUnitConverter.CostToMinutes(cost, _costUnit);
                    routes.Add(new ClosestFacilityRoute(
                        incidentId,
                        facilityId,
                        rank,
                        geometry,
                        length,
                        costMinutes,
                        [new RouteDirectionStep($"Incident {incidentId} - Facility {facilityId}", length, costMinutes, "straight")]));
                    rank++;
                }
            }

            activity?.SetTag("honua.routing.solved", routes.Count > 0);
            activity?.SetTag("honua.routing.routes", routes.Count);
            return new ClosestFacilitySolveResult(routes);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(
        OdCostMatrixSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = RoutingTelemetry.Source.StartActivity("routing.solveOdCostMatrix");
        activity?.SetTag("honua.routing.provider", ProviderName);
        activity?.SetTag("honua.routing.origins", request.Origins.Count);
        activity?.SetTag("honua.routing.destinations", request.Destinations.Count);
        activity?.SetTag("honua.routing.od_output_type", request.OutputType.ToString());
        activity?.SetTag("honua.routing.out_srid", request.OutSrid);

        try
        {
            if (request.Origins.Count == 0 || request.Destinations.Count == 0)
            {
                activity?.SetTag("honua.routing.solved", false);
                return new OdCostMatrixSolveResult([]);
            }

            await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var dataset = await ResolveDatasetAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.dataset", dataset.Id);
            var profile = ResolveTravelProfile(dataset, request.TravelMode);
            activity?.SetTag("honua.routing.travel_profile", profile.Name);

            var blockedEdges = await ResolveBlockedEdgesAsync(
                session, dataset, request.Barriers, request.InSrid, cancellationToken).ConfigureAwait(false);
            var edgesSql = ApplyBlockedEdgeFilter(OutboundEdgesSql(dataset, profile), blockedEdges);

            var originVertices = await SnapAllAsync(
                session, dataset, request.Origins, request.InSrid, cancellationToken).ConfigureAwait(false);
            var destVertices = await SnapAllAsync(
                session, dataset, request.Destinations, request.InSrid, cancellationToken).ConfigureAwait(false);

            var lines = new List<OdLine>();
            for (var originId = 0; originId < originVertices.Count; originId++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var originVid = originVertices[originId].VertexId;
                if (originVid is null)
                {
                    continue;
                }

                var destVids = destVertices
                    .Where(d => d.VertexId is not null)
                    .Select(d => d.VertexId!.Value)
                    .Distinct()
                    .ToArray();
                if (destVids.Length == 0)
                {
                    continue;
                }

                var costs = await OneToManyCostAsync(
                    session, edgesSql, originVid.Value, destVids, cancellationToken).ConfigureAwait(false);

                // The cutoff arrives in minutes; convert it to the raw cost unit so it
                // compares against the pgRouting aggregate costs.
                var cutoffCost = request.Cutoff is { } cutoffMinutes
                    ? RoutingCostUnitConverter.MinutesToCost(cutoffMinutes, _costUnit)
                    : (double?)null;

                var perOrigin = new List<(int DestinationId, double Cost)>();
                for (var destId = 0; destId < destVertices.Count; destId++)
                {
                    var destVid = destVertices[destId].VertexId;
                    if (destVid is null || !costs.TryGetValue(destVid.Value, out var cost))
                    {
                        continue;
                    }

                    if (cutoffCost is { } cutoff && cost > cutoff)
                    {
                        continue;
                    }

                    perOrigin.Add((destId, cost));
                }

                var ranked = perOrigin.OrderBy(x => x.Cost).AsEnumerable();
                if (request.DestinationCount is { } k && k > 0)
                {
                    ranked = ranked.Take(k);
                }

                var rank = 1;
                foreach (var (destinationId, cost) in ranked)
                {
                    // Emit the impedance in travel-time minutes (OdLine.TotalCostMinutes).
                    var costMinutes = RoutingCostUnitConverter.CostToMinutes(cost, _costUnit);
                    lines.Add(new OdLine(originId, destinationId, rank, costMinutes, 0));
                    rank++;
                }
            }

            if (request.OutputType == OdLineOutputType.StraightLines && lines.Count > 0)
            {
                var geometries = await MaterializeOdStraightLinesAsync(
                    session, request, lines, cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < lines.Count; index++)
                {
                    lines[index] = lines[index] with { GeometryGeoJson = geometries[index] };
                }
            }

            activity?.SetTag("honua.routing.solved", lines.Count > 0);
            activity?.SetTag("honua.routing.lines", lines.Count);
            return new OdCostMatrixSolveResult(lines);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Materializes all requested OD straight lines in one parameterized query.
    /// This keeps the cost-only path unchanged and avoids a per-cell reprojection
    /// query while delegating CRS handling to the same PostGIS substrate used by
    /// route and service-area geometry.
    /// </summary>
    private static async Task<IReadOnlyList<string>> MaterializeOdStraightLinesAsync(
        IDatabaseSession session,
        OdCostMatrixSolveRequest request,
        List<OdLine> lines,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                pair.ord,
                ST_AsGeoJSON(
                    ST_Transform(
                        ST_SetSRID(
                            ST_MakeLine(
                                ST_MakePoint(pair.origin_x, pair.origin_y),
                                ST_MakePoint(pair.destination_x, pair.destination_y)),
                            @in_srid),
                        @out_srid)) AS geojson
            FROM unnest(
                @origin_x::double precision[],
                @origin_y::double precision[],
                @destination_x::double precision[],
                @destination_y::double precision[])
                WITH ORDINALITY AS pair(origin_x, origin_y, destination_x, destination_y, ord)
            ORDER BY pair.ord;
            """;

        var originX = new double[lines.Count];
        var originY = new double[lines.Count];
        var destinationX = new double[lines.Count];
        var destinationY = new double[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var origin = request.Origins[line.OriginId];
            var destination = request.Destinations[line.DestinationId];
            originX[index] = origin.Lon;
            originY[index] = origin.Lat;
            destinationX[index] = destination.Lon;
            destinationY[index] = destination.Lat;
        }

        var geometries = new List<string>(lines.Count);
        await foreach (var geometry in session.QueryAsync(
                           sql,
                           static row => row.IsNull(1) ? string.Empty : row.GetFieldValue<string>(1),
                           new Dictionary<string, object?>
                           {
                               ["origin_x"] = originX,
                               ["origin_y"] = originY,
                               ["destination_x"] = destinationX,
                               ["destination_y"] = destinationY,
                               ["in_srid"] = request.InSrid,
                               ["out_srid"] = request.OutSrid,
                           },
                           cancellationToken).ConfigureAwait(false))
        {
            geometries.Add(geometry);
        }

        if (geometries.Count != lines.Count || geometries.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("PostGIS did not materialize every requested OD straight line.");
        }

        return geometries;
    }

    /// <inheritdoc />
    public async Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(
        LocationAllocationSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = RoutingTelemetry.Source.StartActivity("routing.solveLocationAllocation");
        activity?.SetTag("honua.routing.provider", ProviderName);
        activity?.SetTag("honua.routing.facilities", request.Facilities.Count);
        activity?.SetTag("honua.routing.demand_points", request.DemandPoints.Count);
        activity?.SetTag("honua.routing.problem_type", request.ProblemType.ToString());

        try
        {
            if (request.Facilities.Count == 0 || request.DemandPoints.Count == 0)
            {
                activity?.SetTag("honua.routing.solved", false);
                return new LocationAllocationSolveResult([], [], 0, 0);
            }

            await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var dataset = await ResolveDatasetAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("honua.routing.dataset", dataset.Id);
            var profile = ResolveTravelProfile(dataset, request.TravelMode);
            activity?.SetTag("honua.routing.travel_profile", profile.Name);

            var blockedEdges = await ResolveBlockedEdgesAsync(
                session, dataset, request.Barriers, request.InSrid, cancellationToken).ConfigureAwait(false);
            var edgesSql = ApplyBlockedEdgeFilter(OutboundEdgesSql(dataset, profile), blockedEdges);

            // Build the candidate-facility × demand-point impedance matrix:
            // matrix[facility][demand] = network cost (PositiveInfinity when
            // unreachable / beyond the cutoff). Demand -> facility distance equals
            // facility -> demand over the outbound graph for a symmetric profile; we
            // probe from each facility vertex to all demand vertices.
            var facilityVertices = await SnapAllAsync(
                session, dataset, request.Facilities, request.InSrid, cancellationToken).ConfigureAwait(false);
            var demandVertices = await SnapAllAsync(
                session, dataset, request.DemandPoints.Select(d => d.Location).ToList(), request.InSrid, cancellationToken)
                .ConfigureAwait(false);

            var matrix = new double[request.Facilities.Count][];
            for (var f = 0; f < request.Facilities.Count; f++)
            {
                matrix[f] = new double[request.DemandPoints.Count];
                Array.Fill(matrix[f], double.PositiveInfinity);

                var facilityVid = facilityVertices[f].VertexId;
                if (facilityVid is null)
                {
                    continue;
                }

                var demandVids = demandVertices
                    .Where(d => d.VertexId is not null)
                    .Select(d => d.VertexId!.Value)
                    .Distinct()
                    .ToArray();
                if (demandVids.Length == 0)
                {
                    continue;
                }

                var costs = await OneToManyCostAsync(
                    session, edgesSql, facilityVid.Value, demandVids, cancellationToken).ConfigureAwait(false);
                for (var d = 0; d < request.DemandPoints.Count; d++)
                {
                    var demandVid = demandVertices[d].VertexId;
                    if (demandVid is not null && costs.TryGetValue(demandVid.Value, out var cost))
                    {
                        // The solver works in travel-time minutes: it compares against
                        // the request's minute ImpedanceCutoff and emits ImpedanceMinutes,
                        // so the raw cost is converted to minutes here.
                        matrix[f][d] = RoutingCostUnitConverter.CostToMinutes(cost, _costUnit);
                    }
                }
            }

            var result = LocationAllocationSolver.Solve(request, matrix);
            activity?.SetTag("honua.routing.solved", result.ChosenFacilityIds.Count > 0);
            activity?.SetTag("honua.routing.chosen_facilities", result.ChosenFacilityIds.Count);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Snaps a list of points to their nearest graph vertices, preserving input order
    /// (null vertex id for a point that could not be snapped).
    /// </summary>
    private static async Task<IReadOnlyList<(RoutePoint Point, long? VertexId)>> SnapAllAsync(
        IDatabaseSession session,
        NetworkDataset dataset,
        IReadOnlyList<RoutePoint> points,
        int inSrid,
        CancellationToken cancellationToken)
    {
        var snapped = new (RoutePoint, long?)[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vertexId = await SnapToNearestVertexAsync(session, dataset, points[i], inSrid, cancellationToken)
                .ConfigureAwait(false);
            snapped[i] = (points[i], vertexId);
        }

        return snapped;
    }

    /// <summary>
    /// Runs a one-to-many <c>pgr_dijkstraCost</c> from a single source vertex to the
    /// supplied destination vertices over the (already barrier-filtered) edges-SQL,
    /// returning a destination-vertex → aggregate-cost map. The edges-SQL is bound as
    /// a parameter; the source/destination vertex ids are server-derived bound
    /// arrays.
    /// </summary>
    private static async Task<IReadOnlyDictionary<long, double>> OneToManyCostAsync(
        IDatabaseSession session,
        string edgesSql,
        long sourceVertex,
        long[] destinationVertices,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT end_vid, agg_cost
            FROM pgr_dijkstraCost(
                @edges_sql,
                @source, @targets, directed => true);
            """;

        var costs = new Dictionary<long, double>();
        await foreach (var row in session.QueryAsync(
                           sql,
                           static row => (
                               EndVid: row.GetFieldValue<long>(0),
                               Cost: row.GetFieldValue<double>(1)),
                           new Dictionary<string, object?>
                           {
                               ["edges_sql"] = edgesSql,
                               ["source"] = sourceVertex,
                               ["targets"] = destinationVertices,
                           },
                           cancellationToken).ConfigureAwait(false))
        {
            // pgr_dijkstraCost returns the minimum agg_cost per (start,end) pair.
            costs[row.EndVid] = row.Cost;
        }

        return costs;
    }

    private static async Task<long?> SnapToNearestVertexAsync(
        IDatabaseSession session,
        NetworkDataset dataset,
        RoutePoint point,
        int inSrid,
        CancellationToken cancellationToken)
    {
        // KNN nearest-vertex snap using the GiST <-> operator on the dataset's vertex
        // table. The probe point is built in the request SRID then transformed to the
        // graph SRID (4326); when @in_srid = 4326 the transform is an identity no-op.
        // The vertex table name is a validated schema-qualified identifier (not a user
        // value), safe to embed.
        var sql = $"""
            SELECT id
            FROM {dataset.VertexTable}
            ORDER BY the_geom <-> ST_Transform(
                ST_SetSRID(ST_MakePoint(@lon, @lat), @in_srid), 4326)
            LIMIT 1;
            """;

        return await session.QuerySingleOrDefaultAsync<long?>(
                sql,
                static row => row.IsNull(0) ? null : row.GetFieldValue<long>(0),
                new Dictionary<string, object?>
                {
                    ["lon"] = point.Lon,
                    ["lat"] = point.Lat,
                    ["in_srid"] = inSrid,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<(IReadOnlyList<RouteStep> Steps, double Cost)?> SolveDijkstraLegAsync(
        IDatabaseSession session,
        string edgesSql,
        long sourceVertex,
        long targetVertex,
        CancellationToken cancellationToken)
    {
        // pgr_dijkstra over the ways edge table. The inner edges_sql is built by
        // BuildRouteEdgesSql from a constant base plus a barrier-exclusion clause
        // that lists only server-derived integer edge gids (never a user string),
        // so no user value is interpolated. source/target are bound parameters.
        // edge = -1 marks the synthetic final row pgr_dijkstra appends, which we
        // skip. We capture the per-step node alongside the edge so geometry can be
        // threaded in traversal order with correct orientation.
        var sql = $"""
            SELECT node, edge, cost
            FROM pgr_dijkstra(
                '{edgesSql}',
                @source, @target, directed => true)
            WHERE edge <> -1
            ORDER BY seq;
            """;

        var steps = new List<RouteStep>();
        var cost = 0.0;
        var any = false;

        await foreach (var row in session.QueryAsync(
                           sql,
                           static row => new
                           {
                               Node = row.IsNull(0) ? (long?)null : row.GetFieldValue<long>(0),
                               Edge = row.IsNull(1) ? (long?)null : row.GetFieldValue<long>(1),
                               Cost = row.IsNull(2) ? (double?)null : row.GetFieldValue<double>(2),
                           },
                           new Dictionary<string, object?>
                           {
                               ["source"] = sourceVertex,
                               ["target"] = targetVertex,
                           },
                           cancellationToken).ConfigureAwait(false))
        {
            any = true;

            if (row.Node is { } node && row.Edge is { } edge)
            {
                steps.Add(new RouteStep(node, edge));
            }

            if (row.Cost is { } rowCost)
            {
                cost += rowCost;
            }
        }

        return any ? (steps, cost) : null;
    }

    private static async Task<(string GeometryGeoJson, double LengthMeters)> MergeEdgeGeometryAsync(
        IDatabaseSession session,
        NetworkDataset dataset,
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
        // parameters. The edge table name is a validated identifier.
        var sql = $"""
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
                JOIN {dataset.EdgeTable} w ON w.gid = s.gid
            )
            SELECT
                ST_AsGeoJSON(ST_Transform(ST_MakeLine(the_geom ORDER BY ord), @out_srid)) AS geojson,
                COALESCE(SUM(ST_Length(the_geom::geography)), 0) AS length_m
            FROM oriented;
            """;

        var result = await session.QuerySingleOrDefaultAsync<(string GeometryGeoJson, double LengthMeters)>(
                sql,
                static row => (
                    row.IsNull(0) ? string.Empty : row.GetFieldValue<string>(0),
                    row.IsNull(1) ? 0 : row.GetFieldValue<double>(1)),
                new Dictionary<string, object?>
                {
                    ["edge_ids"] = steps.Select(s => s.Edge).ToArray(),
                    ["nodes"] = steps.Select(s => s.Node).ToArray(),
                    ["out_srid"] = outSrid,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    private static async Task<string> SolveServiceAreaRingAsync(
        IDatabaseSession session,
        NetworkDataset dataset,
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
        // edges-SQL is selected by travel direction (outbound vs. reversed graph) and
        // bound as a parameter; the vertex table name is a validated identifier.
        // ST_ConcaveHull yields a non-polygon (point or line) when fewer than three
        // non-collinear vertices are reachable; the type guard returns NULL (mapped to
        // empty) so the caller can skip it.
        var sql = $"""
            WITH reachable AS (
                SELECT dd.node
                FROM pgr_drivingDistance(
                    @edges_sql,
                    @facility, @break_cost, directed => true) AS dd
            ),
            reachable_pts AS (
                SELECT v.the_geom
                FROM reachable r
                JOIN {dataset.VertexTable} v ON v.id = r.node
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

        return await session.QuerySingleOrDefaultAsync(
                sql,
                static row => row.IsNull(0) ? string.Empty : row.GetFieldValue<string>(0),
                new Dictionary<string, object?>
                {
                    ["edges_sql"] = edgesSql,
                    ["facility"] = facilityVertex,
                    ["break_cost"] = breakCost,
                    ["out_srid"] = outSrid,
                },
                cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>
    /// Resolves the set of graph edge gids restricted by the supplied barriers by
    /// transforming each barrier geometry to the graph SRID and testing it against
    /// the <c>ways</c> edges. Point barriers block the single nearest edge; line
    /// and polygon barriers block every edge they intersect. Returns an empty list
    /// when there are no barriers (the common case), so the unrestricted graph is
    /// used unchanged.
    /// </summary>
    /// <remarks>
    /// Barrier geometries are passed as a parallel pair of bound array parameters
    /// (GeoJSON text + per-barrier kind code) and reconstructed in SQL via
    /// <c>ST_GeomFromGeoJSON</c>; no user value is interpolated into SQL. The
    /// returned gids are server-derived integers, safe to embed in the inner
    /// pgr_dijkstra edges-SQL string (which cannot reference bound parameters).
    /// </remarks>
    private static async Task<IReadOnlyList<long>> ResolveBlockedEdgesAsync(
        IDatabaseSession session,
        NetworkDataset dataset,
        IReadOnlyList<RouteBarrier> barriers,
        int inSrid,
        CancellationToken cancellationToken)
    {
        if (barriers.Count == 0)
        {
            return [];
        }

        // Per-barrier parallel arrays: GeoJSON geometry text and the integer kind
        // code (0=point, 1=line, 2=polygon). Both bind as array parameters.
        var geoJson = new string[barriers.Count];
        var kinds = new int[barriers.Count];
        for (var i = 0; i < barriers.Count; i++)
        {
            geoJson[i] = barriers[i].GeometryGeoJson;
            kinds[i] = (int)barriers[i].Kind;
        }

        // Each barrier is transformed from the request SRID to the graph SRID
        // (4326). A point barrier (kind 0) restricts the single nearest edge (KNN
        // <-> on the GiST index); a line/polygon barrier (kind 1/2) restricts every
        // edge it intersects. DISTINCT collapses overlapping barriers.
        var sql = $"""
            WITH input AS (
                SELECT
                    ST_Transform(ST_SetSRID(ST_GeomFromGeoJSON(g.geojson), @in_srid), 4326) AS geom,
                    k.kind AS kind
                FROM unnest(@geojson) WITH ORDINALITY AS g(geojson, ord)
                JOIN unnest(@kinds)   WITH ORDINALITY AS k(kind, ord) USING (ord)
            ),
            point_blocked AS (
                SELECT nearest.gid
                FROM input i
                CROSS JOIN LATERAL (
                    SELECT w.gid
                    FROM {dataset.EdgeTable} w
                    ORDER BY w.the_geom <-> i.geom
                    LIMIT 1
                ) AS nearest
                WHERE i.kind = 0
            ),
            shape_blocked AS (
                SELECT w.gid
                FROM input i
                JOIN {dataset.EdgeTable} w ON ST_Intersects(w.the_geom, i.geom)
                WHERE i.kind <> 0
            )
            SELECT gid FROM point_blocked
            UNION
            SELECT gid FROM shape_blocked;
            """;

        var gids = new List<long>();
        await foreach (var gid in session.QueryAsync(
                           sql,
                           static row => row.GetFieldValue<long>(0),
                           new Dictionary<string, object?>
                           {
                               ["geojson"] = geoJson,
                               ["kinds"] = kinds,
                               ["in_srid"] = inSrid,
                           },
                           cancellationToken).ConfigureAwait(false))
        {
            gids.Add(gid);
        }

        return gids;
    }

    /// <summary>
    /// Builds the inner pgr_dijkstra edges-SQL for the dataset, appending a
    /// barrier-exclusion <c>WHERE</c> clause listing the server-derived blocked edge
    /// gids. Only the validated edge-table identifier and integers (formatted
    /// invariantly) are embedded; no user string ever enters the SQL text.
    /// </summary>
    private static string BuildRouteEdgesSql(
        NetworkDataset dataset,
        RoutingTravelProfile profile,
        IReadOnlyList<long> blockedEdges)
        => ApplyBlockedEdgeFilter(RouteBaseEdgesSqlFor(dataset, profile), blockedEdges);

    /// <summary>
    /// Appends a <c>WHERE gid NOT IN (...)</c> exclusion of the blocked edge gids to
    /// a base edges-SQL. The base SQL has no existing <c>WHERE</c>, so a fresh
    /// clause is appended. Returns the base SQL unchanged when nothing is blocked.
    /// </summary>
    private static string ApplyBlockedEdgeFilter(string baseEdgesSql, IReadOnlyList<long> blockedEdges)
    {
        if (blockedEdges.Count == 0)
        {
            return baseEdgesSql;
        }

        // gids are server-derived BIGINTs; format invariantly and join. This is the
        // pgRouting-idiomatic exclusion: the inner edges-SQL is a string the
        // function re-parses, so it cannot reference bound parameters — only safe,
        // server-derived integer literals are embedded here.
        var idList = string.Join(
            ",",
            blockedEdges.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return $"{baseEdgesSql} WHERE gid NOT IN ({idList})";
    }

    /// <summary>
    /// One step of a Dijkstra leg: the node visited and the edge traversed from it.
    /// Carrying the node lets the geometry merge orient each edge to start at the
    /// step's node, preserving traversal order across legs and revisits.
    /// </summary>
    private readonly record struct RouteStep(long Node, long Edge);
}
