// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Barrier exclusion must not inline every intersected edge gid into the edges
/// SQL that pgRouting re-parses on each call (#6158).
/// </summary>
[Trait("Tier", "Fast")]
public sealed partial class PgRoutingBarrierSqlTests
{
    private const string CoveringPolygon = """
        {"type":"Polygon","coordinates":[[[-1,-1],[1,-1],[1,1],[-1,-1]]]}
        """;

    [Fact]
    public async Task SolveRoute_PolygonBarrier_KeepsEdgesSqlBounded()
    {
        var session = new BarrierSqlSession(blockedRowCount: 50_000);
        var provider = new PgRoutingProvider(new SingleSessionFactory(session), NullLogger<PgRoutingProvider>.Instance);

        var result = await provider.SolveRouteAsync(
            new RouteSolveRequest(
            [
                new RoutePoint(0, 0),
                new RoutePoint(0.01, 0),
                new RoutePoint(0.02, 0.02),
            ])
            {
                Barriers = [new RouteBarrier(RouteBarrierKind.Polygon, CoveringPolygon)],
            },
            CancellationToken.None);

        Assert.True(result.Solved);
        Assert.Equal(1, session.Sql.Count(sql => sql.Contains("CREATE TEMP TABLE", StringComparison.Ordinal)));
        Assert.Equal(1, session.Sql.Count(sql => sql.Contains("INSERT INTO pg_temp.honua_blocked_edges", StringComparison.Ordinal)));

        var legs = session.Sql.Where(IsDijkstraLeg).ToList();
        Assert.Equal(2, legs.Count);
        foreach (var sql in legs)
        {
            Assert.Contains("pg_temp.honua_blocked_edges", sql, StringComparison.Ordinal);
            Assert.DoesNotMatch(InlinedGidList(), sql);
            Assert.True(sql.Length < 2_000, $"edges SQL grew to {sql.Length} characters");
        }
    }

    [Fact]
    public async Task SolveRoute_WithoutBarriers_DoesNotCreateBlockedEdgeTable()
    {
        var session = new BarrierSqlSession(blockedRowCount: 0);
        var provider = new PgRoutingProvider(new SingleSessionFactory(session), NullLogger<PgRoutingProvider>.Instance);

        var result = await provider.SolveRouteAsync(
            new RouteSolveRequest([new RoutePoint(0, 0), new RoutePoint(0.01, 0)]),
            CancellationToken.None);

        Assert.True(result.Solved);
        Assert.DoesNotContain(session.Sql, sql => sql.Contains("honua_blocked_edges", StringComparison.Ordinal));
    }

    private static bool IsDijkstraLeg(string sql)
        => sql.Contains("pgr_dijkstra(", StringComparison.Ordinal)
           && !sql.Contains("pgr_dijkstraCost", StringComparison.Ordinal);

    [GeneratedRegex(@"NOT IN \(\d", RegexOptions.CultureInvariant)]
    private static partial Regex InlinedGidList();

    private sealed class SingleSessionFactory(BarrierSqlSession session) : IDatabaseSessionFactory
    {
        public Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IDatabaseSession>(session);

        public Task<IDatabaseSession> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class BarrierSqlSession(int blockedRowCount) : IDatabaseSession
    {
        public List<string> Sql { get; } = [];

        public string ConnectionString => "scripted";

        public bool IsTransactional => false;

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Sql.Add(sql);
            if (sql.Contains("INSERT INTO pg_temp.honua_blocked_edges", StringComparison.Ordinal))
            {
                return Task.FromResult(blockedRowCount);
            }

            if (sql.Contains("CREATE TEMP TABLE", StringComparison.Ordinal)
                || sql.Contains("DELETE FROM pg_temp.honua_blocked_edges", StringComparison.Ordinal))
            {
                return Task.FromResult(0);
            }

            throw new InvalidOperationException("Unexpected routing SQL: " + sql);
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

            if (sql.Contains("pgr_dijkstra", StringComparison.Ordinal))
            {
                var source = Convert.ToInt64(args!["source"], System.Globalization.CultureInfo.InvariantCulture);
                return [[source, 10L, 1d]];
            }

            if (sql.Contains("ST_MakeLine", StringComparison.Ordinal))
            {
                return [["{\"type\":\"LineString\",\"coordinates\":[[0,0],[0.02,0.02]]}", 100d]];
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
