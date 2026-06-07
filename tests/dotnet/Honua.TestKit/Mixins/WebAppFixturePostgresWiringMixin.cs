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
        services.RemoveAll<ICrsDetectionService>();
        services.RemoveAll<IFileImportService>();
        services.RemoveAll<ISqlFilterTranslator>();
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
        IEnumerable<Action<IServiceCollection>> userConfigurations)
    {
        RemovePostgresBackedServices(services);

        var testConfiguration = BuildPostgresTestConfiguration(connectionString);
        Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

        OverrideNonMultiplexingDataSource(services, connectionString);

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
        IDictionary<string, string?>? extraConfiguration = null)
    {
        RemovePostgresBackedServices(services);

        var testConfiguration = BuildPostgresTestConfiguration(connectionString, extraConfiguration);
        Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

        OverrideNonMultiplexingDataSource(services, connectionString);

        // Replace the Postgres-backed Metadata v2 provider with an in-memory fixture so
        // endpoints read the seeded snapshot without a migrated snapshot row being
        // present in the test database.
        WebAppFixtureMetadataV2Mixin.RegisterDefaultMetadataV2Graph(services);
    }
}
