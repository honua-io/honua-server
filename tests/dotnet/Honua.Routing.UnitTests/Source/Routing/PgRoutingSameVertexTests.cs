// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Same-vertex solves without a database. The scripted session returns no
/// pgRouting row when start = end, which is pgRouting 3.x behaviour (#6157).
/// </summary>
[Trait("Tier", "Fast")]
public sealed class PgRoutingSameVertexTests
{
    [Fact]
    public async Task SolveRoute_SameVertexLeg_KeepsTheEarlierPath()
    {
        var session = new ScriptedRoutingSession();
        var provider = CreateProvider(session);

        var result = await provider.SolveRouteAsync(
            new RouteSolveRequest(
            [
                new RoutePoint(0, 0),
                new RoutePoint(0.01, 0),
                new RoutePoint(0.01, 0),
            ]),
            CancellationToken.None);

        Assert.True(result.Solved);
        Assert.Contains("LineString", result.RouteGeometryGeoJson, StringComparison.Ordinal);
        Assert.Equal(1, result.TotalTimeMinutes, precision: 3);
        // The repeated stop is not sent to pgr_dijkstra; only the real leg is.
        Assert.Equal(
            1,
            session.Sql.Count(sql => sql.Contains("pgr_dijkstra(", StringComparison.Ordinal)
                && !sql.Contains("pgr_dijkstraCost", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SolveRoute_OnlySameVertex_ReturnsZeroCostDegeneratePath()
    {
        var session = new ScriptedRoutingSession();
        var provider = CreateProvider(session);

        var result = await provider.SolveRouteAsync(
            new RouteSolveRequest([new RoutePoint(0, 0), new RoutePoint(0, 0)]),
            CancellationToken.None);

        Assert.True(result.Solved);
        Assert.Equal(0, result.TotalTimeMinutes, precision: 3);
        Assert.Equal(0, result.TotalLengthMeters, precision: 3);
        Assert.Contains("LineString", result.RouteGeometryGeoJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SolveClosestFacility_CoLocatedFacility_RanksAtCostZero()
    {
        var session = new ScriptedRoutingSession();
        var provider = CreateProvider(session);

        var result = await provider.SolveClosestFacilityAsync(
            new ClosestFacilitySolveRequest(
                [new RoutePoint(0, 0)],
                [new RoutePoint(0, 0), new RoutePoint(0.02, 0.02)]),
            CancellationToken.None);

        var route = Assert.Single(result.Routes);
        Assert.Equal(0, route.FacilityId);
        Assert.Equal(1, route.Rank);
        Assert.Equal(0, route.TotalTimeMinutes, precision: 3);
    }

    [Fact]
    public async Task SolveOdCostMatrix_OriginEqualsDestination_EmitsZeroCostLine()
    {
        var session = new ScriptedRoutingSession();
        var provider = CreateProvider(session);

        var result = await provider.SolveOdCostMatrixAsync(
            new OdCostMatrixSolveRequest([new RoutePoint(0, 0)], [new RoutePoint(0, 0)]),
            CancellationToken.None);

        var line = Assert.Single(result.Lines);
        Assert.Equal(0, line.OriginId);
        Assert.Equal(0, line.DestinationId);
        Assert.Equal(0, line.TotalCostMinutes, precision: 3);
    }

    [Fact]
    public async Task SolveLocationAllocation_DemandOnFacility_IsReachableAtCostZero()
    {
        var session = new ScriptedRoutingSession();
        var provider = CreateProvider(session);

        var result = await provider.SolveLocationAllocationAsync(
            new LocationAllocationSolveRequest(
                [new RoutePoint(0, 0)],
                [new DemandPoint(new RoutePoint(0, 0))]),
            CancellationToken.None);

        var allocation = Assert.Single(result.Allocations);
        Assert.Equal(0, allocation.AllocatedFacilityId);
        Assert.Equal(0, allocation.ImpedanceMinutes, precision: 3);
    }

    private static PgRoutingProvider CreateProvider(ScriptedRoutingSession session)
        => new(new SingleSessionFactory(session), NullLogger<PgRoutingProvider>.Instance);

    private sealed class SingleSessionFactory(ScriptedRoutingSession session) : IDatabaseSessionFactory
    {
        public Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IDatabaseSession>(session);

        public Task<IDatabaseSession> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ScriptedRoutingSession : IDatabaseSession
    {
        public List<string> Sql { get; } = [];

        public string ConnectionString => "scripted";

        public bool IsTransactional => false;

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Sql.Add(sql);
            return Task.FromResult(0);
        }

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(sql);

        public IAsyncEnumerable<T> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(sql);

        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            Func<IDatabaseRow, T> rowMapper,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Sql.Add(sql);
            var rows = RowsFor(sql, parameters);
            if (rows.Count == 0)
            {
                return Task.FromResult<T?>(default);
            }

            return Task.FromResult<T?>(rowMapper(new ScriptedRow(rows[0])));
        }

        public async IAsyncEnumerable<T> QueryAsync<T>(
            string sql,
            Func<IDatabaseRow, T> rowMapper,
            object? parameters = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sql.Add(sql);
            foreach (var values in RowsFor(sql, parameters))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return rowMapper(new ScriptedRow(values));
            }

            await Task.CompletedTask;
        }

        public Task<IDatabaseSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static List<object?[]> RowsFor(string sql, object? parameters)
        {
            var args = parameters as IReadOnlyDictionary<string, object?>;
            if (sql.Contains("ways_vertices_pgr", StringComparison.Ordinal))
            {
                var lon = Convert.ToDouble(args!["lon"], System.Globalization.CultureInfo.InvariantCulture);
                var lat = Convert.ToDouble(args["lat"], System.Globalization.CultureInfo.InvariantCulture);
                return [[VertexFor(lon, lat)]];
            }

            if (sql.Contains("pgr_dijkstraCost", StringComparison.Ordinal))
            {
                var source = Convert.ToInt64(args!["source"], System.Globalization.CultureInfo.InvariantCulture);
                var targets = (long[])args["targets"]!;
                var rows = new List<object?[]>();
                foreach (var target in targets)
                {
                    // pgRouting omits start = end. Any other pair has cost 1.
                    if (target != source)
                    {
                        rows.Add([target, 1d]);
                    }
                }

                return rows;
            }

            if (sql.Contains("pgr_dijkstra", StringComparison.Ordinal))
            {
                var source = Convert.ToInt64(args!["source"], System.Globalization.CultureInfo.InvariantCulture);
                var target = Convert.ToInt64(args["target"], System.Globalization.CultureInfo.InvariantCulture);
                if (source == target)
                {
                    return [];
                }

                return [[source, 10L, 1d]];
            }

            if (sql.Contains("ST_MakeLine", StringComparison.Ordinal) && sql.Contains("WITH steps", StringComparison.Ordinal))
            {
                return [["{\"type\":\"LineString\",\"coordinates\":[[0,0],[0.01,0]]}", 100d]];
            }

            if (sql.Contains("ST_MakeLine", StringComparison.Ordinal))
            {
                return [["{\"type\":\"LineString\",\"coordinates\":[[0,0],[0,0]]}"]];
            }

            throw new InvalidOperationException("Unexpected routing SQL: " + sql);
        }

        private static long VertexFor(double lon, double lat)
        {
            if (Math.Abs(lat) < 1e-9 && Math.Abs(lon) < 1e-9)
            {
                return 1;
            }

            if (Math.Abs(lat) < 1e-9 && Math.Abs(lon - 0.01) < 1e-9)
            {
                return 2;
            }

            if (Math.Abs(lat - 0.02) < 1e-9 && Math.Abs(lon - 0.02) < 1e-9)
            {
                return 9;
            }

            return 1;
        }
    }

    private sealed class ScriptedRow(object?[] values) : IDatabaseRow
    {
        public int FieldCount => values.Length;

        public bool IsNull(int ordinal) => values[ordinal] is null;

        public T GetFieldValue<T>(int ordinal) => (T)values[ordinal]!;

        public int GetOrdinal(string name) => throw new NotSupportedException(name);
    }
}
