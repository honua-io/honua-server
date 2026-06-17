// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Honua.TestKit.Mixins;

/// <summary>
/// Audit-A2 mixin: the PostgreSQL/Npgsql test-host wiring that <see cref="WebAppFixture"/>
/// previously inlined twice (once in the isolated-per-test branch, once in the
/// shared-server branch) as ~120 LOC of duplicated <c>UseSetting</c>,
/// <c>ConfigureAppConfiguration</c>, and <c>ConfigureTestServices</c> blocks. These
/// concerns are independent of the fixture's HTTP client, schema-isolation, and seed
/// surface, so they live here as static helpers that both bootstrap paths call.
/// </summary>
/// <remarks>
/// Per structural-audit-2026-05 (group A2), this is the fourth slice of the
/// WebAppFixture mixin split. Behaviour is byte-identical to the previous inline
/// implementations — this is a pure relocation that de-duplicates the isolated and
/// shared bootstrap paths.
/// </remarks>
internal static class WebAppFixturePostgresWiringMixin
{
    private const string StableTestGeocodingBaseUrl = "https://8.8.8.8/nominatim";
    private const string TestEncryptionMasterKey =
        "test-master-key-that-is-at-least-32-characters-long-for-security";
    private const string TestEncryptionSalt =
        "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=";

    /// <summary>
    /// Applies the auth-bypass and migration-skip <c>UseSetting</c> flags that both the
    /// isolated and shared bootstrap paths set on the host builder. honua-server#1144
    /// requires BOTH <c>HONUA_DEV_AUTH</c> and <c>HONUA_DEV_AUTH_ALLOW_BYPASS</c> to be
    /// set — the single flag is insufficient outside of Test+explicit-ack to prevent
    /// accidental bypass activation in Staging/QA.
    /// </summary>
    internal static void ApplyCommonHostSettings(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "true");
        builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
        builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
    }

    /// <summary>
    /// Builds the in-memory app configuration dictionary that both bootstrap paths
    /// supply to <see cref="IWebHostBuilder.ConfigureAppConfiguration"/>. The optional
    /// <paramref name="extraSettings"/> let the shared bootstrap layer in its
    /// admin-password and schema-header flags without duplicating the common entries.
    /// </summary>
    internal static IDictionary<string, string?> BuildAppConfigurationDictionary(
        string connectionString,
        IDictionary<string, string?>? extraSettings = null)
    {
        var attachmentsPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "attachments");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:honua"] = connectionString,
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            // Avoid live DNS dependency during startup validation in integration tests.
            ["Geocoding:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
            ["Geocoding:Providers:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
            ["HONUA_SKIP_MIGRATIONS"] = "true",
            ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
            ["Limits:Connections:RequestTimeout"] = "00:05:00",
            ["Limits:Query:QueryTimeout"] = "00:02:00",
            ["FileStorage:Provider"] = "Local",
            ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
            ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
            ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt,
        };

        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings)
            {
                settings[key] = value;
            }
        }

        return settings;
    }

    /// <summary>
    /// Removes the PostgreSQL-backed service registrations that both bootstrap paths
    /// strip before re-registering against the test connection string. Kept here so the
    /// list of stripped services lives in one place and additions stay in lockstep
    /// between the isolated and shared paths.
    /// </summary>
    internal static void RemovePostgresBackedServices(IServiceCollection services)
    {
        services.RemoveAll<NpgsqlDataSource>();
        services.RemoveAll<IFeatureReader>();
        services.RemoveAll<IFeatureWriter>();
        services.RemoveAll<ITileProvider>();
        services.RemoveAll<IRelationshipStore>();
        services.RemoveAll<IStreamingFeatureStore>();
        services.RemoveAll<IAttachmentStore>();
        services.RemoveAll<ITableDiscoveryService>();
        services.RemoveAll<IDatabaseHealthChecker>();
        services.RemoveAll<IDatabaseConnectionProvider>();
        // IAdoNetDatabaseConnectionProvider (ADR-0046) is a cast-factory forwarding to
        // IDatabaseConnectionProvider; remove it together so no stale descriptor survives
        // pointing at a removed dependency.
        services.RemoveAll<IAdoNetDatabaseConnectionProvider>();
        services.RemoveAll<ICrsDetectionService>();
        services.RemoveAll<IFileImportService>();
        services.RemoveAll<ISqlFilterTranslator>();
    }

    /// <summary>
    /// Timer-based background DB pollers/reconcilers that should not run in the test host.
    /// Matched by implementation-type name to avoid the TestKit referencing internal host types.
    /// </summary>
    /// <remarks>
    /// Limited to <c>MigrationBatchBackgroundService</c>: its logic is exercised directly by
    /// <c>MigrationBatchEndpointTests</c> via a recording orchestrator, so nothing depends on the
    /// timer loop, and it was the confirmed source of the cross-schema
    /// <c>honua.migration_batch_runs</c> "relation does not exist" noise. The Deploy/ExecutionJob/
    /// FeatureChange reconcilers are intentionally NOT disabled — ControlPlane integration tests
    /// submit work and wait for those background loops to advance it, so removing them hangs the
    /// "Infra and Security" shard. Gating additional pollers requires per-poller verification.
    /// </remarks>
    private static readonly HashSet<string> _disabledTestHostPollers = new(StringComparer.Ordinal)
    {
        "MigrationBatchBackgroundService",
    };

    /// <summary>
    /// Removes the timer-based background DB pollers/reconcilers from the test host. Under the
    /// parallelized schema-isolated test shards (#1359/#1532) these poll the database every few
    /// seconds from outside any request scope — so they cannot pick up the per-test schema —
    /// producing cross-schema "relation does not exist" log noise and background DB contention
    /// multiplied across every parallel test server. Their reconciliation logic is exercised
    /// directly by dedicated tests (e.g. <c>MigrationBatchEndpointTests</c> via a recording
    /// orchestrator, and the ControlPlane/Events reconciler unit tests), so the timer wrapper
    /// serves no purpose in the WebAppFixture host.
    /// </summary>
    internal static void RemoveBackgroundPollers(IServiceCollection services)
    {
        var pollers = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType is { } implementation &&
                _disabledTestHostPollers.Contains(implementation.Name))
            .ToList();

        foreach (var descriptor in pollers)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>
    /// Builds the minimal in-memory <see cref="IConfiguration"/> that
    /// <c>Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices</c> reads to
    /// re-register the PostgreSQL-backed services. The optional
    /// <paramref name="extraSettings"/> let the shared bootstrap layer in its
    /// schema-header flag without duplicating the common entries.
    /// </summary>
    internal static IConfiguration BuildPostgresTestConfiguration(
        string connectionString,
        IDictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
            ["Limits:Connections:RequestTimeout"] = "00:05:00",
            ["Limits:Query:QueryTimeout"] = "00:02:00",
            ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
            ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt,
        };

        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings)
            {
                settings[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    /// <summary>
    /// Replaces the multiplexing-enabled <see cref="NpgsqlDataSource"/> registered by
    /// <c>AddPostgreSqlServices</c> with a non-multiplexing one bound to
    /// <paramref name="connectionString"/>. Schema-based tests need session state, which
    /// multiplexing breaks.
    /// </summary>
    internal static void OverrideNonMultiplexingDataSource(
        IServiceCollection services,
        string connectionString)
    {
        services.RemoveAll<NpgsqlDataSource>();
        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.ConnectionStringBuilder.Multiplexing = false;
            return dataSourceBuilder.Build();
        });
    }

    /// <summary>
    /// Replaces <see cref="IDatabaseConnectionProvider"/> with the test
    /// implementation that observes the fixture's current schema via the supplied
    /// accessor delegate. The delegate captures the fixture's mutable
    /// <c>_currentSchema</c> so per-test schema isolation continues to work after the
    /// host has already been built.
    /// </summary>
    internal static void OverrideDatabaseConnectionProvider(
        IServiceCollection services,
        Func<string?> currentSchemaAccessor)
    {
        services.RemoveAll<IDatabaseConnectionProvider>();
        services.AddScoped<IDatabaseConnectionProvider>(serviceProvider =>
        {
            var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
            return new TestDatabaseConnectionProvider(dataSource, currentSchemaAccessor);
        });
    }

    /// <summary>
    /// Replaces the singleton-shape <see cref="NpgsqlDataSource"/> registration with a
    /// per-test-database routing registration used by template-database isolation
    /// (<c>HONUA_TEST_DB_TEMPLATE=1</c>). The router resolves the active database from the
    /// ambient request schema (which carries the test's database name in this mode), so
    /// every DB-access path — the connection providers AND services that inject
    /// <see cref="NpgsqlDataSource"/> directly — routes to the test's own cloned database.
    /// </summary>
    /// <remarks>
    /// The router is a process-scoped resource (one pooled data source per live test
    /// database); it is registered as a singleton keyed off the owning
    /// <see cref="PostgresFixture"/> and the data source resolves it per scope. Disabling
    /// the template flag leaves this path untouched.
    /// </remarks>
    internal static void OverrideTemplateDatabaseRouting(
        IServiceCollection services,
        PostgresFixture fixture,
        string connectionString)
    {
        // One router per host. The fallback (no ambient test DB in scope: background work /
        // header-less requests) targets the bootstrap database with multiplexing disabled,
        // matching the schema-mode override. The router owns + caches the per-database data
        // sources (a DI singleton, disposed at host teardown).
        var fallbackConnectionString = BuildNonMultiplexingConnectionString(connectionString);
        var router = new Infrastructure.TemplateDatabaseDataSourceRouter(fixture, fallbackConnectionString);
        services.AddSingleton(router);
        fixture.AttachRouter(router);

        // Hot path: route DB access through a router-backed connection provider that opens
        // from the router's cached per-test-database data source. Mirrors the isolated path's
        // IDatabaseConnectionProvider override so the secure decorator wraps it transparently.
        // This avoids a scoped NpgsqlDataSource registration whose container-owned disposal
        // would dispose the router's cached data source at the first scope's end.
        services.RemoveAll<IDatabaseConnectionProvider>();
        services.AddScoped<IDatabaseConnectionProvider>(serviceProvider =>
            new Infrastructure.TemplateDatabaseConnectionProvider(
                serviceProvider.GetRequiredService<Infrastructure.TemplateDatabaseDataSourceRouter>(),
                fixture));

        // Direct NpgsqlDataSource consumers (off the Stac hot path) get a per-scope,
        // DI-owned data source for the active database so the container can safely dispose it
        // without touching the router's cached instances.
        services.RemoveAll<NpgsqlDataSource>();
        services.AddScoped<NpgsqlDataSource>(serviceProvider =>
            serviceProvider.GetRequiredService<Infrastructure.TemplateDatabaseDataSourceRouter>()
                .CreateOwnedForCurrentDatabase());
    }

    private static string BuildNonMultiplexingConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Multiplexing = false };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Applies the full ConfigureTestServices block used by the isolated bootstrap
    /// path: strip the Postgres-backed registrations, register them against the test
    /// connection string, override the data source to disable multiplexing, register
    /// the in-memory Metadata v2 graph, override the database connection provider,
    /// then run the fixture-supplied service configurations.
    /// </summary>
    internal static void ConfigureIsolatedTestServices(
        IServiceCollection services,
        string connectionString,
        Func<string?> currentSchemaAccessor,
        IEnumerable<Action<IServiceCollection>> userConfigurations,
        PostgresFixture? templateFixture = null)
    {
        RemovePostgresBackedServices(services);
        RemoveBackgroundPollers(services);

        var testConfiguration = BuildPostgresTestConfiguration(connectionString);
        Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

        if (templateFixture is not null)
        {
            // Template-database mode: route every DB-access path to the test's own cloned
            // database via the ambient-schema-keyed router instead of one shared data source.
            OverrideTemplateDatabaseRouting(services, templateFixture, connectionString);
        }
        else
        {
            OverrideNonMultiplexingDataSource(services, connectionString);
        }

        // Replace the Postgres-backed Metadata v2 provider with an in-memory fixture so
        // endpoints read the seeded snapshot without a migrated snapshot row being
        // present in the test database.
        WebAppFixtureMetadataV2Mixin.RegisterDefaultMetadataV2Graph(services);

        OverrideDatabaseConnectionProvider(services, currentSchemaAccessor);

        foreach (var configure in userConfigurations)
        {
            configure(services);
        }
    }

    /// <summary>
    /// Applies the ConfigureTestServices block used by the shared bootstrap path. Same
    /// as the isolated path minus the database-connection-provider override (the shared
    /// server uses schema-routing HTTP headers instead of a fixture-mutable accessor)
    /// and minus per-fixture user configurations (the shared server has none).
    /// </summary>
    internal static void ConfigureSharedTestServices(
        IServiceCollection services,
        string connectionString,
        IDictionary<string, string?>? extraConfiguration = null,
        PostgresFixture? templateFixture = null)
    {
        RemovePostgresBackedServices(services);
        RemoveBackgroundPollers(services);

        var testConfiguration = BuildPostgresTestConfiguration(connectionString, extraConfiguration);
        Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

        if (templateFixture is not null)
        {
            // Template-database mode: route every DB-access path to the test's own cloned
            // database via the ambient-schema-keyed router instead of one shared data source.
            OverrideTemplateDatabaseRouting(services, templateFixture, connectionString);
        }
        else
        {
            OverrideNonMultiplexingDataSource(services, connectionString);
        }

        // Replace the Postgres-backed Metadata v2 provider with an in-memory fixture so
        // endpoints read the seeded snapshot without a migrated snapshot row being
        // present in the test database.
        WebAppFixtureMetadataV2Mixin.RegisterDefaultMetadataV2Graph(services);
    }
}
