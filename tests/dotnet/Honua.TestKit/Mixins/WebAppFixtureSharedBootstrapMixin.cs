// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Honua.TestKit.Mixins;

/// <summary>
/// Audit-A2 mixin: the shared-server bootstrap that <see cref="WebAppFixture"/>
/// historically inlined as ~85 LOC of <c>InitializeSharedAsync</c> plus a ~35-LOC
/// shared-state teardown branch in <c>DisposeAsync</c>. The shared server is process-wide
/// static state — exactly one <see cref="WebApplicationFactory{TEntryPoint}"/> and one
/// <see cref="PostgresFixture"/> serve every WebAppFixture that didn't request a
/// custom service configuration — so its lifecycle is naturally a static-state owner
/// and lives here.
/// </summary>
/// <remarks>
/// Per structural-audit-2026-05 (group A2), this is the sixth slice of the WebAppFixture
/// mixin split. Behaviour is byte-identical — the shared semaphore, refcount, and
/// initialised-flag move 1:1 onto this mixin. The WebAppFixture instance still owns its
/// own _currentSchema, _serviceScope, and Client; only the truly process-wide
/// shared-server resources moved.
/// </remarks>
internal static class WebAppFixtureSharedBootstrapMixin
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static WebApplicationFactory<Program>? _sharedFactory;
    private static PostgresFixture? _sharedPostgres;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

    /// <summary>
    /// Returns the currently-initialised shared factory, or throws if the shared
    /// bootstrap hasn't run yet. Used by per-fixture client/scope creation.
    /// </summary>
    internal static WebApplicationFactory<Program> Factory
        => _sharedFactory ?? throw new InvalidOperationException("Shared web factory not initialized.");

    /// <summary>
    /// Returns the currently-initialised shared <see cref="PostgresFixture"/>, or throws
    /// if the shared bootstrap hasn't run yet. Used by the WebAppFixture's
    /// <c>Postgres</c> accessor when running in shared mode.
    /// </summary>
    internal static PostgresFixture Postgres
        => _sharedPostgres ?? throw new InvalidOperationException("Shared Postgres fixture not initialized.");

    /// <summary>
    /// Reference-counted bootstrap of the process-wide shared server + postgres. The
    /// first caller spins up the factory and connects it to a fresh
    /// <see cref="PostgresFixture"/>; every subsequent caller just bumps the refcount.
    /// </summary>
    internal static async Task EnsureInitializedAsync(string sharedAdminPassword)
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!_sharedInitialized)
            {
                Environment.SetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS", "true");

                _sharedPostgres = new PostgresFixture();
                await _sharedPostgres.InitializeAsync();

                _sharedFactory = BuildSharedFactory(_sharedPostgres.ConnectionString, sharedAdminPassword);

                _sharedInitialized = true;
            }

            _sharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    /// <summary>
    /// Reference-counted teardown of the shared server + postgres. The last caller
    /// flushes the factory and the Postgres fixture; every other caller just decrements
    /// the refcount.
    /// </summary>
    internal static async Task ReleaseAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (_sharedRefCount > 0)
            {
                _sharedRefCount--;
            }

            if (_sharedRefCount == 0 && _sharedInitialized)
            {
                if (_sharedFactory is not null)
                {
                    await _sharedFactory.DisposeAsync();
                }

                if (_sharedPostgres is not null)
                {
                    await _sharedPostgres.DisposeAsync();
                }

                _sharedFactory = null;
                _sharedPostgres = null;
                _sharedInitialized = false;
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    private static WebApplicationFactory<Program> BuildSharedFactory(
        string connectionString,
        string sharedAdminPassword)
    {
        var sharedAppConfigExtras = new Dictionary<string, string?>
        {
            ["HONUA_DEV_AUTH"] = "true",
            ["HONUA_DEV_AUTH_ALLOW_BYPASS"] = "true",
            ["HONUA_ADMIN_PASSWORD"] = sharedAdminPassword,
            ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
            ["Database:QueryCache:EnableAutomaticCaching"] = "false",
        };

        var sharedPostgresConfigExtras = new Dictionary<string, string?>
        {
            ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
        };

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                WebAppFixturePostgresWiringMixin.ApplyCommonHostSettings(builder);
                builder.UseSetting("HONUA_ADMIN_PASSWORD", sharedAdminPassword);

                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(
                        WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                            connectionString,
                            sharedAppConfigExtras));
                });

                builder.ConfigureTestServices(services =>
                {
                    WebAppFixturePostgresWiringMixin.ConfigureSharedTestServices(
                        services,
                        connectionString,
                        sharedPostgresConfigExtras);
                });
            });
    }
}
