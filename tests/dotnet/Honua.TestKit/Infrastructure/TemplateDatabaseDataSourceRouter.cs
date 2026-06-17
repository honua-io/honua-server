// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Infrastructure.Middleware;
using Npgsql;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Per-database <see cref="NpgsqlDataSource"/> router used by the faster template-database
/// test-isolation mode (<c>HONUA_TEST_DB_TEMPLATE=1</c>).
/// </summary>
/// <remarks>
/// <para>
/// In template-database mode every test runs against its own physical database cloned from
/// a shared template (see <see cref="PostgresFixture.CreateIsolatedDatabaseAsync"/>). The
/// shared test host, however, has exactly one <see cref="NpgsqlDataSource"/> registration
/// that every DB-access path resolves. This router resolves the active database from the
/// ambient <see cref="SchemaContext"/> that <c>TestSchemaMiddleware</c> populates from the
/// <c>X-Honua-Test-Schema</c> header — which, in template mode, carries the test's database
/// name. Routing therefore covers <em>every</em> DB-access path uniformly (the connection
/// providers AND the handful of services that inject <see cref="NpgsqlDataSource"/>
/// directly), without touching production routing code.
/// </para>
/// <para>
/// The <see cref="NpgsqlDataSource"/> registration that calls <see cref="Resolve"/> is
/// <em>scoped</em>, so the .NET DI container owns and disposes the returned data source at
/// the end of each request scope. The router therefore returns a <b>fresh</b> data source
/// per call (never a shared/cached instance) so a scope's disposal can never dispose a data
/// source still in use by another scope. Each per-database data source caps its pool small
/// (<see cref="PostgresFixture.BuildConnectionStringForDatabase"/>) so a wide parallel run
/// stays within the cluster's connection budget.
/// </para>
/// <para>
/// Feature tables live in <c>public</c> and metadata in the cross-schema <c>honua</c> schema
/// inside each cloned database, so no per-request <c>SET search_path</c> rewrite is needed:
/// PostgreSQL silently skips the (non-existent) ambient-named schema and resolves
/// <c>features</c> through <c>public</c>, which the default data-source search_path includes.
/// </para>
/// </remarks>
internal sealed class TemplateDatabaseDataSourceRouter : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _fallbackConnectionString;
    private readonly Lazy<NpgsqlDataSource> _fallbackDataSource;
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _byDatabase =
        new(StringComparer.Ordinal);

    public TemplateDatabaseDataSourceRouter(PostgresFixture fixture, string fallbackConnectionString)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _fallbackConnectionString = fallbackConnectionString
            ?? throw new ArgumentNullException(nameof(fallbackConnectionString));
        _fallbackDataSource = new Lazy<NpgsqlDataSource>(
            () => NpgsqlDataSource.Create(_fallbackConnectionString),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Creates a data source for the ambient request database. When no per-test database is
    /// in scope (background work, or requests issued without the routing header) a data
    /// source for the default/bootstrap database is returned so behaviour matches a normal
    /// connection. The caller (the scoped DI registration) owns and disposes the result.
    /// </summary>
    /// <summary>
    /// Resolves the (cached, router-owned) data source for the ambient request database.
    /// </summary>
    /// <remarks>
    /// The returned data source is owned and disposed by this router (a DI <em>singleton</em>),
    /// never by the resolving scope — callers must NOT dispose it. They only open and dispose
    /// individual connections. This is why per-DB routing must flow through a router-backed
    /// connection provider rather than a scoped <c>NpgsqlDataSource</c> registration (a
    /// scoped factory result would be disposed by the container at the end of the first
    /// request scope, breaking every later scope that shares the cached instance).
    /// </remarks>
    public NpgsqlDataSource Resolve()
    {
        var ambient = SchemaContext.AmbientCurrentSchema;

        // Route per-database ONLY when the ambient name is a database this fixture cloned in
        // template mode. Other ambient values are classic per-test SCHEMA names (e.g. a
        // UseSeed fixture sharing this host) — those use the bootstrap data source, and the
        // connection providers' SET search_path keeps schema isolation working.
        if (string.IsNullOrWhiteSpace(ambient) || !_fixture.IsIsolatedDatabase(ambient))
        {
            return _fallbackDataSource.Value;
        }

        return _byDatabase.GetOrAdd(
            ambient,
            name => NpgsqlDataSource.Create(_fixture.BuildConnectionStringForDatabase(name)));
    }

    /// <summary>
    /// Creates a fresh, caller-owned data source for the ambient request database. Used by the
    /// scoped <see cref="NpgsqlDataSource"/> registration that serves the handful of services
    /// injecting a data source directly (off the hot path). The DI container owns and disposes
    /// the returned instance at scope end, so it must NOT be a router-cached instance.
    /// </summary>
    public NpgsqlDataSource CreateOwnedForCurrentDatabase()
    {
        var ambient = SchemaContext.AmbientCurrentSchema;
        var connectionString = !string.IsNullOrWhiteSpace(ambient) && _fixture.IsIsolatedDatabase(ambient)
            ? _fixture.BuildConnectionStringForDatabase(ambient)
            : _fallbackConnectionString;

        return NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// Disposes and removes the cached data source for a dropped per-test database so its
    /// pooled connections are released. Called from the fixture when the database is dropped.
    /// </summary>
    public void RemoveDatabase(string databaseName)
    {
        if (_byDatabase.TryRemove(databaseName, out var dataSource))
        {
            dataSource.Dispose();
        }
    }

    public void Dispose()
    {
        if (_fallbackDataSource.IsValueCreated)
        {
            _fallbackDataSource.Value.Dispose();
        }

        foreach (var dataSource in _byDatabase.Values)
        {
            dataSource.Dispose();
        }

        _byDatabase.Clear();
    }
}
