// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Service-area polygons are cumulative disks labeled from 0 to each break (#6026).
/// </summary>
[Trait("Tier", "Fast")]
public sealed class PgRoutingServiceAreaBreakTests
{
    [Fact]
    public async Task SolveServiceArea_MultipleBreaks_LabelsEachCumulativeDiskFromZero()
    {
        var session = new ServiceAreaSession();
        var provider = new PgRoutingProvider(new SingleSessionFactory(session), NullLogger<PgRoutingProvider>.Instance);

        var result = await provider.SolveServiceAreaAsync(
            new ServiceAreaSolveRequest([new RoutePoint(0, 0)], [1.0, 2.0]),
            CancellationToken.None);

        Assert.Equal(2, result.Polygons.Count);
        Assert.Equal(0d, result.Polygons[0].FromBreak);
        Assert.Equal(1d, result.Polygons[0].ToBreak);
        Assert.Equal(0d, result.Polygons[1].FromBreak);
        Assert.Equal(2d, result.Polygons[1].ToBreak);
        Assert.All(session.Sql, sql => Assert.DoesNotContain("ST_Difference", sql, StringComparison.Ordinal));
        Assert.Contains(session.Sql, sql => sql.Contains("ST_ConcaveHull", StringComparison.Ordinal));
    }

    private sealed class SingleSessionFactory(ServiceAreaSession session) : IDatabaseSessionFactory
    {
        public Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IDatabaseSession>(session);

        public Task<IDatabaseSession> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ServiceAreaSession : IDatabaseSession
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
            // The concave-hull query joins ways_vertices_pgr, so match it before the snap.
            if (sql.Contains("pgr_drivingDistance", StringComparison.Ordinal))
            {
                return Task.FromResult<T?>(rowMapper(new ScriptedRow(
                    ["{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,0]]]}"])));
            }

            if (sql.Contains("ways_vertices_pgr", StringComparison.Ordinal))
            {
                return Task.FromResult<T?>(rowMapper(new ScriptedRow([1L])));
            }

            throw new InvalidOperationException("Unexpected routing SQL: " + sql);
        }

        public async IAsyncEnumerable<T> QueryAsync<T>(
            string sql,
            Func<IDatabaseRow, T> rowMapper,
            object? parameters = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sql.Add(sql);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public Task<IDatabaseSession> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedRow(object?[] values) : IDatabaseRow
    {
        public int FieldCount => values.Length;

        public bool IsNull(int ordinal) => values[ordinal] is null;

        public T GetFieldValue<T>(int ordinal) => (T)values[ordinal]!;

        public int GetOrdinal(string name) => throw new NotSupportedException(name);
    }
}
