// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Configuration;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Attachments;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.TestKit.Mixins;

internal static class WebAppFixturePostgresWiringMixin
{
    private const string StableTestGeocodingBaseUrl = "https://8.8.8.8/nominatim";
    private static readonly Lazy<string> _temporaryFilesDirectory =
        new(() => Directory.CreateTempSubdirectory("honua-test-temp-files-").FullName);

    // Keep material stable for this test process, including host restart proofs, but
    // do not feed shipped example credentials to fixtures running as Production.
    private static readonly string _testEncryptionMasterKey =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static readonly string _testEncryptionSalt =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    internal static void ApplyCommonHostSettings(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "true");
        builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
        builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
    }

    internal static IDictionary<string, string?> BuildAppConfigurationDictionary(
        string connectionString,
        IDictionary<string, string?>? extraSettings = null)
    {
        // "tmp" and "attachments" are fixed literals, so this combine can't drop the
        // current directory.
        var attachmentsPath = Path.Join(Directory.GetCurrentDirectory(), "tmp", "attachments");
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
            // The shipped default is the fixed /tmp/honua-temp, which another OS user on a shared
            // CI host can own; each test process keeps temporary downloads in its own directory.
            ["TemporaryFiles:StorageDirectory"] = _temporaryFilesDirectory.Value,
            ["FileStorage:Provider"] = "Local",
            ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
            ["Security:ConnectionEncryption:MasterKey"] = _testEncryptionMasterKey,
            ["Security:ConnectionEncryption:Salt"] = _testEncryptionSalt,
            // Request-supplied secret references are refused unless an operator permits them;
            // integration tests name variables under this prefix.
            ["Security:RequestSecretReferences:AllowedEnvironmentVariablePrefixes:0"] = "HONUA_TEST_",
            // The ops-health rollup sampler (#2553) is a hosted background service that would
            // otherwise boot in EVERY integration-test host and add constant background
            // Postgres sampling/flush/downsample/prune load across the whole parallel
            // Server.Tests fleet — enough to time out already-contended CI shards. Default it
            // OFF in test hosts; the rollup's own tests opt back in explicitly (see
            // OpsObservabilityEndpointsTests) and the production default stays enabled.
            ["Observability:OpsHealthRollup:Enabled"] = "false",
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

    internal static void RemovePostgresBackedServices(IServiceCollection services)
    {
        services.RemoveAll<NpgsqlDataSource>();
        services.RemoveAll<IFeatureReader>();
        services.RemoveAll<IFeatureWriter>();
        services.RemoveAll<ITileProvider>();
        services.RemoveAll<IRelationshipStore>();
        services.RemoveAll<IStreamingFeatureStore>();
        services.RemoveAll<IAttachmentStore>();
        services.RemoveAll<IFeatureAttachmentCleanup>();
        services.RemoveAll<PostgresAttachmentStore>();
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

    private static readonly HashSet<string> _disabledTestHostPollers = new(StringComparer.Ordinal)
    {
        "MigrationBatchBackgroundService",
    };

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
            ["Security:ConnectionEncryption:MasterKey"] = _testEncryptionMasterKey,
            ["Security:ConnectionEncryption:Salt"] = _testEncryptionSalt,
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

    internal static void OverrideNonMultiplexingDataSource(
        IServiceCollection services,
        string connectionString)
    {
        services.RemoveAll<NpgsqlDataSource>();
        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.ConnectionStringBuilder.Multiplexing = false;
            // Mirror production (PostgresDataSourceFactory): keep the password in the string
            // exposed by NpgsqlDataSource.ConnectionString so IDatabaseConnectionProvider
            // .GetConnectionString() returns a value that can open a fresh connection. Without
            // this, the import snapshot-refresh / publish-materialize paths reconnect with a
            // passwordless string and fail SCRAM auth ("No password has been provided but the
            // backend requires one (in SASL/SCRAM-SHA-256)") in the FileImport integration shard.
            dataSourceBuilder.ConnectionStringBuilder.PersistSecurityInfo = true;
            return dataSourceBuilder.Build();
        });
    }

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

    internal static void ConfigureIsolatedTestServices(
        IServiceCollection services,
        string connectionString,
        Func<string?> currentSchemaAccessor,
        IEnumerable<Action<IServiceCollection>> userConfigurations)
    {
        RemovePostgresBackedServices(services);
        RemoveBackgroundPollers(services);

        // The isolated host owns its fixture schema, so configure it as an operational schema
        // exactly as an operator would (#5391): a secure connection whose search path names that
        // schema then resolves under the unchanged connection policy. Drop the composition root's
        // schema configuration so AddPostgreSqlServices registers this one.
        services.RemoveAll<Honua.Db.Postgres.Features.Infrastructure.PostgresSchemaConfiguration>();
        var testConfiguration = BuildPostgresTestConfiguration(
            connectionString,
            BuildFixtureOperationalSchemaSettings(currentSchemaAccessor()));
        Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
            services,
            testConfiguration,
            Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest);
        UseFixtureSchemaGuardBypass(services);

        OverrideNonMultiplexingDataSource(services, connectionString);
        BindFixtureAttachmentStore(services);

        // Mirror the production composition root: wrap the provider raster-coverage
        // renderer with the vector-aware styled-render decorator (honua-server#2498) so
        // integration tests exercise the same IRasterMapRenderer the running server uses.
        Honua.Infrastructure.Rendering.VectorAwareRasterMapRendererServiceCollectionExtensions
            .AddVectorAwareRasterMapRendering(services);

        // Replace the Postgres-backed Metadata v2 provider with an in-memory fixture so
        // endpoints read the seeded snapshot without a migrated snapshot row being
        // present in the test database.
        WebAppFixtureMetadataV2Mixin.RegisterDefaultMetadataV2Graph(services);

        OverrideDatabaseConnectionProvider(services, currentSchemaAccessor);
        // Job executors open a new scope and publish into the fixture schema. The HTTP
        // test-schema header never reaches that scope, so bind ISchemaContext to the
        // same accessor the connection provider uses.
        services.RemoveAll<ISchemaContext>();
        services.AddScoped<ISchemaContext>(_ => new FixtureCurrentSchemaContext(currentSchemaAccessor));

        foreach (var configure in userConfigurations)
        {
            configure(services);
        }
    }

    internal static IDictionary<string, string?>? BuildFixtureOperationalSchemaSettings(string? fixtureSchema)
        => string.IsNullOrWhiteSpace(fixtureSchema)
            ? null
            : new Dictionary<string, string?> { ["Database:OperationalSchemas:0"] = fixtureSchema };

    internal static void ConfigureSharedTestServices(
        IServiceCollection services,
        string connectionString,
        IDictionary<string, string?>? extraConfiguration = null)
    {
        RemovePostgresBackedServices(services);
        RemoveBackgroundPollers(services);

        var testConfiguration = BuildPostgresTestConfiguration(connectionString, extraConfiguration);
        Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
            services,
            testConfiguration,
            Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest);
        UseFixtureSchemaGuardBypass(services);

        OverrideNonMultiplexingDataSource(services, connectionString);
        BindFixtureAttachmentStore(services);

        // Mirror the production composition root: wrap the provider raster-coverage
        // renderer with the vector-aware styled-render decorator (honua-server#2498) so
        // integration tests exercise the same IRasterMapRenderer the running server uses.
        Honua.Infrastructure.Rendering.VectorAwareRasterMapRendererServiceCollectionExtensions
            .AddVectorAwareRasterMapRendering(services);

        // Replace the Postgres-backed Metadata v2 provider with an in-memory fixture so
        // endpoints read the seeded snapshot without a migrated snapshot row being
        // present in the test database.
        WebAppFixtureMetadataV2Mixin.RegisterDefaultMetadataV2Graph(services);

        // Incremental shared-host seam (#3712): request-local service overrides are
        // selected by a per-fixture opaque header. General ConfigureServices actions
        // still use ConfigureIsolatedTestServices; only the explicit opt-in API can
        // reach this registry, so unsupported registration shapes fail closed.
        services.AddSingleton<ScopedServiceOverrideRegistry>();
        services.AddSingleton<IStartupFilter, ScopedServiceOverrideStartupFilter>();
    }

    private static void BindFixtureAttachmentStore(IServiceCollection services)
    {
        services.RemoveAll<PostgresAttachmentStore>();
        services.AddScoped(serviceProvider => new PostgresAttachmentStore(
            serviceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
            serviceProvider.GetService<ICloudFileStorage>(),
            serviceProvider.GetRequiredService<ILogger<PostgresAttachmentStore>>(),
            schemaName: serviceProvider.GetRequiredService<ISchemaContext>().CurrentSchema,
            orphanLedger: serviceProvider.GetRequiredService<IAttachmentOrphanLedger>(),
            attachmentLimits: serviceProvider.GetService<IOptions<LimitsOptions>>()?.Value.Attachments
                ?? new AttachmentLimits(),
            migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!));
    }

    private static void UseFixtureSchemaGuardBypass(IServiceCollection services)
    {
        services.RemoveAll<IDatabaseSchemaGuard>();
        services.RemoveAll<Honua.Db.Postgres.Features.Infrastructure.Migrations.PostgresCoreSchemaGuard>();
        services.AddSingleton<IDatabaseSchemaGuard>(Honua.TestKit.FixtureBypassDatabaseSchemaGuard.Instance);
    }
}
