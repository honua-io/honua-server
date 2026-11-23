// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.TestKit.Mixins;

// The shared host outlives its last reference for IdleReleaseDelay. A serial test collection
// whose cases each own a default WebAppFixture releases the last reference after every case;
// without the grace each case booted a new host and Postgres fixture. Cases stay isolated
// because every fixture owns a schema it creates on initialization and drops on disposal.
internal static class WebAppFixtureSharedBootstrapMixin
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly IdleReleaseScheduler IdleRelease = new(
        _sharedLock, () => IdleReleaseDelay, DisposeIfIdleAsync, "Shared test host");
    private static WebApplicationFactory<Program>? _sharedFactory;
    private static PostgresFixture? _sharedPostgres;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

    internal static TimeSpan IdleReleaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    internal static WebApplicationFactory<Program> Factory
        => _sharedFactory ?? throw new InvalidOperationException("Shared web factory not initialized.");

    internal static PostgresFixture Postgres
        => _sharedPostgres ?? throw new InvalidOperationException("Shared Postgres fixture not initialized.");

    internal static async Task EnsureInitializedAsync(
        string sharedAdminPassword,
        Func<string, string, WebApplicationFactory<Program>>? buildFactory = null)
    {
        await _sharedLock.WaitAsync();
        try
        {
            ReclaimIdleHost();
            if (!_sharedInitialized)
            {
                try
                {
                    _sharedPostgres = new PostgresFixture();
                    await _sharedPostgres.InitializeAsync();
                    _sharedFactory = (buildFactory ?? BuildSharedFactory)(_sharedPostgres.ConnectionString, sharedAdminPassword);
                    _sharedFactory.StartServer();
                    _sharedInitialized = true;
                    IdleRelease.HookProcessExit();
                }
                catch
                {
                    try
                    {
                        await DisposeSharedResourcesAsync();
                    }
                    catch (Exception)
                    {
                        // Cleanup must not replace the startup error that explains the failure.
                    }
                    throw;
                }
            }

            _sharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }
    }

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
                IdleRelease.Schedule();
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    // Disposes an unreferenced shared host now instead of after the idle grace.
    internal static Task ReleaseIdleNowAsync() => IdleRelease.ReleaseNowAsync();

    // Callers hold _sharedLock; the scheduler has already cancelled any pending release.
    private static async Task DisposeIfIdleAsync()
    {
        if (_sharedRefCount == 0 && _sharedInitialized)
        {
            await DisposeSharedResourcesAsync();
        }
    }

    // A host reused after its last release must look like the fresh host each case used to get:
    // a graph swap a failed case armed for its next read must not fire in the next case.
    private static void ReclaimIdleHost()
    {
        IdleRelease.Cancel();
        if (_sharedInitialized && _sharedRefCount == 0)
        {
            _sharedFactory?.Services.GetService<TestMetadataV2GraphProvider>()?.ClearPendingActivation();
        }
    }

    private static async Task DisposeSharedResourcesAsync()
    {
        var factory = _sharedFactory;
        var postgres = _sharedPostgres;
        _sharedFactory = null;
        _sharedPostgres = null;
        _sharedInitialized = false;
        try
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
        }
        finally
        {
            if (postgres is not null)
            {
                await postgres.DisposeAsync();
            }
        }
    }

    internal static WebApplicationFactory<Program> BuildSharedFactory(
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

        // The shared bootstrap owns this factory until startup fails or it stays unreferenced past the idle grace.
        return Honua.TestKit.ConfiguredWebApplicationFactory.Create(builder =>
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
