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
/// Closest-facility FromFacility geometry must run facility to incident (#6161).
/// </summary>
[Trait("Tier", "Fast")]
public sealed class PgRoutingFromFacilityTests
{
    [Theory]
    [InlineData(ClosestFacilityTravelDirection.FromFacility, true)]
    [InlineData(ClosestFacilityTravelDirection.ToFacility, false)]
    public async Task SolveClosestFacility_TravelDirection_OrientsMergedLine(
        ClosestFacilityTravelDirection direction,
        bool reverseMergedLine)
    {
        var session = new DirectionSession();
        var provider = new PgRoutingProvider(new SingleSessionFactory(session), NullLogger<PgRoutingProvider>.Instance);

        var result = await provider.SolveClosestFacilityAsync(
            new ClosestFacilitySolveRequest(
                [new RoutePoint(0, 0)],
                [new RoutePoint(0.01, 0)],
                Direction: direction),
            CancellationToken.None);

        Assert.Single(result.Routes);
        var merge = Assert.Single(session.Sql, sql => sql.Contains("ST_MakeLine", StringComparison.Ordinal));
        if (reverseMergedLine)
        {
            Assert.Contains("ST_Reverse(ST_MakeLine", merge, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("ST_Reverse(ST_MakeLine", merge, StringComparison.Ordinal);
        }
    }

    private sealed class SingleSessionFactory(DirectionSession session) : IDatabaseSessionFactory
    {
        public Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IDatabaseSession>(session);

        public Task<IDatabaseSession> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class DirectionSession : IDatabaseSession
    {
        public List<string> Sql { get; } = [];

        public string ConnectionString => "scripted";

        public bool IsTransactional => false;

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected routing SQL: " + sql);

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
                return [[Math.Abs(lon) < 1e-9 ? 1L : 2L]];
            }

            if (sql.Contains("pgr_dijkstraCost", StringComparison.Ordinal))
            {
                return [[2L, 1d]];
            }

            if (sql.Contains("pgr_dijkstra", StringComparison.Ordinal))
            {
                var source = Convert.ToInt64(args!["source"], System.Globalization.CultureInfo.InvariantCulture);
                return [[source, 10L, 1d]];
            }

            if (sql.Contains("ST_MakeLine", StringComparison.Ordinal))
            {
                return [["{\"type\":\"LineString\",\"coordinates\":[[0,0],[0.01,0]]}", 100d]];
            }

            throw new InvalidOperationException("Unexpected routing SQL: " + sql);
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
