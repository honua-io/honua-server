// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// pgRouting fixture for the gated <c>PgRoutingProvider</c> integration test
/// (issue #1266). Honors an external connection string via
/// <c>HONUA_ROUTING_TEST_DB_URL</c> (point it at <c>docker/routing/compose.yml</c>);
/// otherwise starts a <c>pgrouting/pgrouting</c> Testcontainer. In both cases it
/// ensures the PostGIS + pgRouting extensions exist and seeds the deterministic
/// 3x3 lattice topology (identical to <c>docker/routing/init-routing.sql</c>) so
/// the provider SQL can be validated against a real graph.
/// </summary>
public sealed class PgRoutingFixture : IAsyncLifetime
{
    /// <summary>
    /// pgRouting image tag (PG17 / PostGIS 3.5 / pgRouting 3.7.3) used when no
    /// external database is supplied. Verified pullable from Docker Hub.
    /// </summary>
    public const string PgRoutingImage = "pgrouting/pgrouting:17-3.5-3.7.3";

    /// <summary>
    /// Environment variable holding an external pgRouting connection string. When
    /// set, the fixture uses that database instead of starting a Testcontainer.
    /// </summary>
    public const string ExternalConnectionStringEnv = "HONUA_ROUTING_TEST_DB_URL";

    private const int InitializationMaxAttempts = 5;

    /// <summary>
    /// The deterministic seed topology. Kept in sync with
    /// <c>docker/routing/init-routing.sql</c>. A 3x3 lattice (vertices 1-9) at
    /// 0.01-degree spacing anchored at (0,0); every edge has cost = reverse_cost
    /// = 1. The least-cost path 1 -> 9 is 4 hops (cost 4).
    /// </summary>
    public const string SeedSql = """
        CREATE EXTENSION IF NOT EXISTS postgis;
        CREATE EXTENSION IF NOT EXISTS pgrouting;

        CREATE TABLE IF NOT EXISTS public.ways_vertices_pgr (
            id        BIGINT PRIMARY KEY,
            cnt       INTEGER,
            chk       INTEGER,
            ein       INTEGER,
            eout      INTEGER,
            the_geom  geometry(Point, 4326)
        );

        CREATE TABLE IF NOT EXISTS public.ways (
            gid           BIGINT PRIMARY KEY,
            source        BIGINT,
            target        BIGINT,
            name          TEXT,
            cost          DOUBLE PRECISION,
            reverse_cost  DOUBLE PRECISION,
            the_geom      geometry(LineString, 4326)
        );

        CREATE INDEX IF NOT EXISTS idx_ways_the_geom_gist
            ON public.ways USING GIST (the_geom);
        CREATE INDEX IF NOT EXISTS idx_ways_source ON public.ways (source);
        CREATE INDEX IF NOT EXISTS idx_ways_target ON public.ways (target);
        CREATE INDEX IF NOT EXISTS idx_ways_vertices_pgr_the_geom_gist
            ON public.ways_vertices_pgr USING GIST (the_geom);

        TRUNCATE public.ways;
        TRUNCATE public.ways_vertices_pgr CASCADE;

        INSERT INTO public.ways_vertices_pgr (id, the_geom) VALUES
         (1, ST_SetSRID(ST_MakePoint(0.00, 0.00), 4326)),
         (2, ST_SetSRID(ST_MakePoint(0.01, 0.00), 4326)),
         (3, ST_SetSRID(ST_MakePoint(0.02, 0.00), 4326)),
         (4, ST_SetSRID(ST_MakePoint(0.00, 0.01), 4326)),
         (5, ST_SetSRID(ST_MakePoint(0.01, 0.01), 4326)),
         (6, ST_SetSRID(ST_MakePoint(0.02, 0.01), 4326)),
         (7, ST_SetSRID(ST_MakePoint(0.00, 0.02), 4326)),
         (8, ST_SetSRID(ST_MakePoint(0.01, 0.02), 4326)),
         (9, ST_SetSRID(ST_MakePoint(0.02, 0.02), 4326))
        ON CONFLICT (id) DO NOTHING;

        INSERT INTO public.ways (gid, source, target, name, cost, reverse_cost, the_geom) VALUES
         (1,  1, 2, 'h1', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.00), ST_MakePoint(0.01,0.00)),4326)),
         (2,  2, 3, 'h2', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.00), ST_MakePoint(0.02,0.00)),4326)),
         (3,  4, 5, 'h3', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.01), ST_MakePoint(0.01,0.01)),4326)),
         (4,  5, 6, 'h4', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.01), ST_MakePoint(0.02,0.01)),4326)),
         (5,  7, 8, 'h5', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.02), ST_MakePoint(0.01,0.02)),4326)),
         (6,  8, 9, 'h6', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.02), ST_MakePoint(0.02,0.02)),4326)),
         (7,  1, 4, 'v1', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.00), ST_MakePoint(0.00,0.01)),4326)),
         (8,  4, 7, 'v2', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.01), ST_MakePoint(0.00,0.02)),4326)),
         (9,  2, 5, 'v3', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.00), ST_MakePoint(0.01,0.01)),4326)),
         (10, 5, 8, 'v4', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.01), ST_MakePoint(0.01,0.02)),4326)),
         (11, 3, 6, 'v5', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.02,0.00), ST_MakePoint(0.02,0.01)),4326)),
         (12, 6, 9, 'v6', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.02,0.01), ST_MakePoint(0.02,0.02)),4326))
        ON CONFLICT (gid) DO NOTHING;
        """;

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;
    private string? _connectionString;

    /// <summary>
    /// Connection string for the seeded pgRouting database.
    /// </summary>
    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("pgRouting fixture not initialized.");

    /// <summary>
    /// Data source for the seeded pgRouting database.
    /// </summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("pgRouting fixture not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        try
        {
            var external = Environment.GetEnvironmentVariable(ExternalConnectionStringEnv);
            if (string.IsNullOrWhiteSpace(external))
            {
                _container = new PostgreSqlBuilder()
                    .WithImage(PgRoutingImage)
                    .WithDatabase("honua_routing")
                    .WithUsername("test")
                    .WithPassword("test")
                    .Build();
                await _container.StartAsync().ConfigureAwait(false);
                _connectionString = _container.GetConnectionString();
            }
            else
            {
                _connectionString = external;
            }

            _dataSource = NpgsqlDataSource.Create(_connectionString);

            await ExecuteWithInitializationRetryAsync(async () =>
            {
                await using var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = SeedSql;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
            _dataSource = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
        }
    }

    private static async Task ExecuteWithInitializationRetryAsync(
        Func<Task> operation,
        int maxAttempts = InitializationMaxAttempts)
    {
        Exception? lastTransient = null;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or NpgsqlException or SocketException)
            {
                lastTransient = ex;
                if (attempt == maxAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(delay.TotalMilliseconds * attempt)).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Failed to initialize pgRouting fixture after {maxAttempts} attempts.",
            lastTransient);
    }
}
