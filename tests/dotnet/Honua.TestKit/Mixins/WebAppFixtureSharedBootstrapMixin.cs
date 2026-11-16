// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Honua.TestKit.Mixins;

internal static class WebAppFixtureSharedBootstrapMixin
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static WebApplicationFactory<Program>? _sharedFactory;
    private static PostgresFixture? _sharedPostgres;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

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
            if (!_sharedInitialized)
            {
                try
                {
                    _sharedPostgres = new PostgresFixture();
                    await _sharedPostgres.InitializeAsync();
                    _sharedFactory = (buildFactory ?? BuildSharedFactory)(_sharedPostgres.ConnectionString, sharedAdminPassword);
                    _sharedFactory.StartServer();
                    _sharedInitialized = true;
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
                await DisposeSharedResourcesAsync();
            }
        }
        finally
        {
            _sharedLock.Release();
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

        // The shared bootstrap owns this factory until startup fails or its last caller releases it.
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
