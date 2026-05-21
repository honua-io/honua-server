// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.TestKit;

/// <summary>
/// Web application fixture for integration tests.
/// Combines WebApplicationFactory with PostgresFixture.
/// Supports service replacement and schema-based isolation.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly SemaphoreSlim _secureConnectionLock = new(1, 1);
    private static WebApplicationFactory<Program>? _sharedFactory;
    private static PostgresFixture? _sharedPostgres;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

    private PostgresFixture? _postgres;
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private Action<IWebHostBuilder>? _configureWebHost;
    private WebApplicationFactory<Program>? _factory;
    private string? _currentSchema;
    private bool _useSharedServer;
    private string? _seedPath;
    private string? _seedProfile;
    private IServiceScope? _serviceScope;

    /// <summary>
    /// Test service ID used for testing operations.
    /// </summary>
    public const string TestServiceId = "test";

    /// <summary>
    /// Test layer ID used for testing operations.
    /// </summary>
    public const int TestLayerId = 0;

    private const string StableTestGeocodingBaseUrl = "https://8.8.8.8/nominatim";
    private const string TestEncryptionMasterKey = "test-master-key-that-is-at-least-32-characters-long-for-security";
    private const string TestEncryptionSalt = "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=";
    private const string TestSecureConnectionName = "test";
    private const string TestSecureConnectionCreatedBy = "test-fixture";

    /// <summary>
    /// Admin password used by <see cref="CreateAdminClient"/> and configured into the
    /// shared test server's <c>HONUA_ADMIN_PASSWORD</c> setting. Exposed so tests that
    /// build their own non-shared <see cref="WebAppFixture"/> (e.g. cross-client
    /// certification fixtures) can wire the same value into their custom web host
    /// configuration without re-declaring a string literal that must stay in lockstep.
    /// </summary>
    public const string SharedAdminPassword = "test-admin-password";
    private static readonly TimeSpan _defaultTestClientTimeout = TimeSpan.FromMinutes(5);

    public WebAppFixture()
    {
    }

    public HttpClient Client { get; private set; } = null!;

    public PostgresFixture Postgres => _useSharedServer
        ? _sharedPostgres ?? throw new InvalidOperationException("Shared Postgres fixture not initialized.")
        : _postgres ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public PostgresFixture PostgresFixture => Postgres;

    public string? CurrentSchema => _currentSchema;

    /// <summary>
    /// Gets the database connection provider for test scenarios.
    /// </summary>
    public IDatabaseConnectionProvider DatabaseConnectionProvider => GetService<IDatabaseConnectionProvider>();

    /// <summary>
    /// Gets the service provider from the test server's DI container.
    /// </summary>
    public IServiceProvider Services => (_useSharedServer ? _sharedFactory : _factory)?.Services
        ?? throw new InvalidOperationException("Web application factory not initialized.");

    private bool HasCustomConfiguration => _serviceConfigurations.Count > 0 || _configureWebHost != null;

    public async Task InitializeAsync()
    {
        _useSharedServer = !HasCustomConfiguration;

        if (_useSharedServer)
        {
            await InitializeSharedAsync();
            return;
        }

        _postgres = new PostgresFixture();
        await _postgres.InitializeAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Configure test environment
                builder.UseEnvironment("Test");

                // Configure authentication bypass for test environment. Wave-2 audit hardening
                // requires the explicit acknowledgement token alongside HONUA_DEV_AUTH=true; see
                // DevAuthBypassStartupValidator.
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseSetting("HONUA_DEV_AUTH_ACK", "i-understand-this-bypasses-auth");
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");

                _configureWebHost?.Invoke(builder);

                // Configure application configuration with test connection string BEFORE app startup
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    var attachmentsPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "attachments");
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:honua"] = _postgres.ConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString,
                        // Avoid live DNS dependency during startup validation in integration tests.
                        ["Geocoding:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                        ["Geocoding:Providers:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                        ["HONUA_SKIP_MIGRATIONS"] = "true",
                        ["Limits:Connections:RequestTimeout"] = "00:05:00",
                        ["Limits:Query:QueryTimeout"] = "00:02:00",
                        ["FileStorage:Provider"] = "Local",
                        ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
                        ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                        ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Remove and re-register all PostgreSQL services with test connection string
                    services.RemoveAll<NpgsqlDataSource>();
                    services.RemoveAll<IFeatureReader>();
                    services.RemoveAll<IFeatureWriter>();
                    services.RemoveAll<ITileProvider>();
                    services.RemoveAll<IRelationshipStore>();
                    services.RemoveAll<IStreamingFeatureStore>();
                    services.RemoveAll<IAttachmentStore>();
                    services.RemoveAll<ILayerCatalog>();
                    services.RemoveAll<ITableDiscoveryService>();
                    services.RemoveAll<IDatabaseHealthChecker>();
                    services.RemoveAll<IDatabaseConnectionProvider>();
                    services.RemoveAll<ICrsDetectionService>();
                    services.RemoveAll<IFileImportService>();
                    services.RemoveAll<ISqlFilterTranslator>();

                    // Create test configuration with connection string
                    var testConfiguration = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString,
                            ["Limits:Connections:RequestTimeout"] = "00:05:00",
                            ["Limits:Query:QueryTimeout"] = "00:02:00",
                            ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                            ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                        })
                        .Build();

                    // Register all PostgreSQL services using the Postgres layer's extension method
                    // This ensures proper dependency injection without Server/TestKit directly instantiating Postgres types
                    Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

                    // Override the data source to avoid multiplexing so schema-based tests keep session state.
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton<NpgsqlDataSource>(_ =>
                    {
                        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.ConnectionString);
                        dataSourceBuilder.ConnectionStringBuilder.Multiplexing = false;
                        return dataSourceBuilder.Build();
                    });

                    // Override specific services for testing
                    services.RemoveAll<ILayerCatalog>();
                    services.AddScoped<ILayerCatalog, Honua.Postgres.Features.Catalog.PostgresLayerCatalog>();

                    // Override database connection provider with test-specific implementation
                    services.RemoveAll<IDatabaseConnectionProvider>();
                    services.AddScoped<IDatabaseConnectionProvider>(serviceProvider =>
                    {
                        var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
                        return new TestDatabaseConnectionProvider(dataSource, () => _currentSchema);
                    });

                    // Apply custom service configurations
                    foreach (var configure in _serviceConfigurations)
                    {
                        configure(services);
                    }
                });
            });

        Client = CreateClient();
        _serviceScope = _factory.Services.CreateScope();

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await _postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
            await SeedSchemaAsync(_currentSchema);
        }

        await EnsureTestSecureConnectionAsync();
    }

    /// <summary>
    /// Ensures a large test dataset exists for streaming performance tests
    /// </summary>
    public async Task EnsureLargeTestDatasetAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            throw new InvalidOperationException("Test schema not initialized.");
        }

        await using var connection = await Postgres.GetConnectionAsync(_currentSchema);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM features WHERE layer_id = @layerId;";
        countCommand.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = TestLayerId });
        var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        if (existingCount >= 2000)
        {
            return;
        }

        var additionalFeaturesNeeded = 2000 - existingCount;

        await using var transaction = await connection.BeginTransactionAsync();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, ST_GeomFromWKB(@geometry, 4326), @attributes::jsonb);
            """;
        insertCommand.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = TestLayerId });
        var geometryParam = new NpgsqlParameter { ParameterName = "@geometry", Value = DBNull.Value };
        var attributesParam = new NpgsqlParameter { ParameterName = "@attributes", Value = DBNull.Value };
        insertCommand.Parameters.Add(geometryParam);
        insertCommand.Parameters.Add(attributesParam);

        for (var i = 0; i < additionalFeaturesNeeded; i++)
        {
            geometryParam.Value = CreateTestPointGeometry(-180 + (i % 360), -90 + ((i / 360) % 180));

            var attributes = new Dictionary<string, object?>
            {
                ["Name"] = $"Streaming Test Feature {i}",
                ["Category"] = "Performance Test",
                ["Value"] = i % 100,
                ["TestBatch"] = "LargeDataset"
            };
            attributesParam.Value = JsonSerializer.Serialize(attributes);

            await insertCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Creates a simple test point geometry as WKB
    /// </summary>
    private static byte[] CreateTestPointGeometry(double x, double y)
    {
        // Create a simple WKB point geometry
        // This is a simplified implementation for testing purposes
        var geometryFactory = new NetTopologySuite.Geometries.GeometryFactory();
        var point = geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(x, y));
        var writer = new NetTopologySuite.IO.WKBWriter();
        return writer.Write(point);
    }

    private async Task InitializeSharedAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!_sharedInitialized)
            {
                Environment.SetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS", "true");

                _sharedPostgres = new PostgresFixture();
                await _sharedPostgres.InitializeAsync();

                var attachmentsPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "attachments");

                _sharedFactory = new WebApplicationFactory<Program>()
                    .WithWebHostBuilder(builder =>
                    {
                        builder.UseEnvironment("Test");
                        builder.UseSetting("HONUA_DEV_AUTH", "true");
                        builder.UseSetting("HONUA_DEV_AUTH_ACK", "i-understand-this-bypasses-auth");
                        builder.UseSetting("HONUA_ADMIN_PASSWORD", SharedAdminPassword);
                        builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");

                        builder.ConfigureAppConfiguration((context, configBuilder) =>
                        {
                            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:honua"] = _sharedPostgres.ConnectionString,
                                ["ConnectionStrings:DefaultConnection"] = _sharedPostgres.ConnectionString,
                                // Avoid live DNS dependency during startup validation in integration tests.
                                ["Geocoding:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                                ["Geocoding:Providers:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                                ["HONUA_DEV_AUTH"] = "true",
                                ["HONUA_DEV_AUTH_ACK"] = "i-understand-this-bypasses-auth",
                                ["HONUA_ADMIN_PASSWORD"] = SharedAdminPassword,
                                ["HONUA_SKIP_MIGRATIONS"] = "true",
                                ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
                                ["Limits:Connections:RequestTimeout"] = "00:05:00",
                                ["Limits:Query:QueryTimeout"] = "00:02:00",
                                ["Database:QueryCache:EnableAutomaticCaching"] = "false",
                                ["FileStorage:Provider"] = "Local",
                                ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
                                ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                                ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                            });
                        });

                        builder.ConfigureTestServices(services =>
                        {
                            services.RemoveAll<NpgsqlDataSource>();
                            services.RemoveAll<IFeatureReader>();
                            services.RemoveAll<IFeatureWriter>();
                            services.RemoveAll<ITileProvider>();
                            services.RemoveAll<IRelationshipStore>();
                            services.RemoveAll<IStreamingFeatureStore>();
                            services.RemoveAll<IAttachmentStore>();
                            services.RemoveAll<ILayerCatalog>();
                            services.RemoveAll<ITableDiscoveryService>();
                            services.RemoveAll<IDatabaseHealthChecker>();
                            services.RemoveAll<IDatabaseConnectionProvider>();
                            services.RemoveAll<ICrsDetectionService>();
                            services.RemoveAll<IFileImportService>();
                            services.RemoveAll<ISqlFilterTranslator>();

                            var testConfiguration = new ConfigurationBuilder()
                                .AddInMemoryCollection(new Dictionary<string, string?>
                                {
                                    ["ConnectionStrings:DefaultConnection"] = _sharedPostgres.ConnectionString,
                                    ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
                                    ["Limits:Connections:RequestTimeout"] = "00:05:00",
                                    ["Limits:Query:QueryTimeout"] = "00:02:00",
                                    ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                                    ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                                })
                                .Build();

                            Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

                            services.RemoveAll<NpgsqlDataSource>();
                            services.AddSingleton<NpgsqlDataSource>(_ =>
                            {
                                var dataSourceBuilder = new NpgsqlDataSourceBuilder(_sharedPostgres.ConnectionString);
                                dataSourceBuilder.ConnectionStringBuilder.Multiplexing = false;
                                return dataSourceBuilder.Build();
                            });

                            services.RemoveAll<ILayerCatalog>();
                            services.AddScoped<ILayerCatalog, Honua.Postgres.Features.Catalog.PostgresLayerCatalog>();
                        });
                    });

                _sharedInitialized = true;
            }

            _sharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await Postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
        }

        await SeedSchemaAsync(_currentSchema);

        Client = CreateAdminClient();
        _serviceScope = _sharedFactory?.Services.CreateScope();

        await EnsureTestSecureConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        _serviceScope = null;

        if (_useSharedServer)
        {
            if (_currentSchema is not null)
            {
                await Postgres.DropSchemaAsync(_currentSchema);
            }

            Client.Dispose();

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

            return;
        }

        if (_currentSchema is not null)
        {
            await Postgres.DropSchemaAsync(_currentSchema);
        }

        Client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// Configure services before initialization (must be called before InitializeAsync).
    /// </summary>
    public WebAppFixture ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Configure the web host before initialization.
    /// </summary>
    public WebAppFixture ConfigureWebHost(Action<IWebHostBuilder> configure)
    {
        _configureWebHost = _configureWebHost == null ? configure : _configureWebHost + configure;
        return this;
    }

    /// <summary>
    /// Configure a seed file to apply when creating the test schema.
    /// Must be called before InitializeAsync.
    /// </summary>
    public WebAppFixture UseSeed(string seedPath, string? profile = null)
    {
        _seedPath = ResolveSeedPath(seedPath);
        _seedProfile = profile;
        return this;
    }

    /// <summary>
    /// Replace a service in the DI container with a test implementation.
    /// </summary>
    public WebAppFixture ReplaceService<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _serviceConfigurations.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddScoped<TService, TImplementation>();
        });
        return this;
    }

    /// <summary>
    /// Replace a service with a specific instance.
    /// </summary>
    public WebAppFixture ReplaceService<TService>(TService instance)
        where TService : class
    {
        _serviceConfigurations.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddSingleton(instance);
        });
        return this;
    }

    /// <summary>
    /// Get a service from the test server's DI container.
    /// </summary>
    public T GetService<T>() where T : notnull
    {
        var provider = _serviceScope?.ServiceProvider
            ?? throw new InvalidOperationException("Service scope not initialized.");

        return provider.GetRequiredService<T>();
    }

    /// <summary>
    /// Get an optional service from the test server's DI container.
    /// </summary>
    public T? GetOptionalService<T>() where T : class
    {
        return _serviceScope?.ServiceProvider.GetService<T>();
    }

    /// <summary>
    /// Get the test secure connection ID created by the fixture.
    /// </summary>
    public async Task<Guid?> GetTestSecureConnectionIdAsync()
    {
        if (_serviceScope is null)
        {
            return null;
        }

        var registry = _serviceScope.ServiceProvider.GetService<ISecureConnectionRegistry>();
        if (registry == null)
        {
            return null;
        }

        var connection = await registry.GetConnectionByNameAsync(TestSecureConnectionName);
        return connection?.ConnectionId;
    }

    /// <summary>
    /// Create an isolated schema for this test.
    /// Schema is automatically cleaned up on dispose.
    /// </summary>
    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        if (!string.IsNullOrWhiteSpace(_currentSchema))
        {
            return _currentSchema;
        }

        _currentSchema = await Postgres.CreateIsolatedSchemaAsync(testClassName);
        await SeedSchemaAsync(_currentSchema);
        await EnsureTestSecureConnectionAsync();
        return _currentSchema;
    }

    private async Task EnsureTestSecureConnectionAsync()
    {
        if (_serviceScope is null)
        {
            return;
        }

        var services = _serviceScope.ServiceProvider;
        var connectionProvider = services.GetRequiredService<IDatabaseConnectionProvider>();

        if (!await SecureConnectionTablesAvailableAsync(connectionProvider).ConfigureAwait(false))
        {
            return;
        }

        await EnsureSecureConnectionProviderColumnAsync(connectionProvider).ConfigureAwait(false);

        var registry = services.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = services.GetRequiredService<IConnectionEncryptionService>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await _secureConnectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await registry.GetConnectionByNameAsync(TestSecureConnectionName).ConfigureAwait(false);
            if (existing != null)
            {
                return;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host) ||
                string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) ||
                builder.Port <= 0)
            {
                return;
            }

            var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString).ConfigureAwait(false);
            var keyVersion = await encryptionService.GetCurrentKeyVersionAsync().ConfigureAwait(false);
            var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
            var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

            var connection = DataConnection.CreateWithEncryptedCredentials(
                name: TestSecureConnectionName,
                host: builder.Host,
                port: builder.Port,
                databaseName: builder.Database,
                username: builder.Username,
                encryptedConnectionString: encrypted,
                encryptionKeyVersion: keyVersion,
                createdBy: TestSecureConnectionCreatedBy,
                description: "Test secure connection",
                sslRequired: sslRequired,
                sslMode: sslMode);

            try
            {
                await registry.CreateConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                // Another fixture created the same connection concurrently.
            }
        }
        finally
        {
            _secureConnectionLock.Release();
        }
    }

    private static async Task<bool> SecureConnectionTablesAvailableAsync(IDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'honua'
              AND table_name = 'data_connections'
            LIMIT 1
            """;
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                return result != null && result != DBNull.Value;
            }
            catch (Exception ex) when (IsTransientSecureConnectionCheckFailure(ex))
            {
                if (attempt == maxAttempts)
                {
                    Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
        return false;
    }

    private static async Task EnsureSecureConnectionProviderColumnAsync(IDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            ALTER TABLE IF EXISTS honua.data_connections
                ADD COLUMN IF NOT EXISTS provider_name TEXT NOT NULL DEFAULT 'postgis';
            """;

        await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static bool IsTransientSecureConnectionCheckFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException;
    }

    private Task SeedSchemaAsync(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(_seedPath))
        {
            return Postgres.ApplySeedAsync(_seedPath, schemaName, _seedProfile);
        }

        return ServerTestData.SeedAsync(Postgres, schemaName);
    }

    private static string ResolveSeedPath(string seedPath)
    {
        if (Path.IsPathRooted(seedPath) || File.Exists(seedPath))
        {
            return seedPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, seedPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return seedPath;
    }


    /// <summary>
    /// Reset database state in the public schema (legacy method).
    /// Prefer schema-based isolation for parallel execution.
    /// </summary>
    public async Task ResetAsync()
    {
        await Postgres.ResetAsync();
    }

    /// <summary>
    /// Creates an <see cref="HttpMessageHandler"/> connected to the in-memory test server.
    /// Useful for constructing custom transports (e.g. gRPC channels) that route
    /// through the same pipeline as <see cref="Client"/>.
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        return factory.Server.CreateHandler();
    }

    /// <summary>
    /// Creates a <see cref="Microsoft.AspNetCore.TestHost.WebSocketClient"/> for testing
    /// WebSocket endpoints through the in-memory test server.
    /// </summary>
    public Microsoft.AspNetCore.TestHost.WebSocketClient CreateWebSocketClient()
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        return factory.Server.CreateWebSocketClient();
    }

    /// <summary>
    /// Create a new HTTP client with custom configuration.
    /// </summary>
    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        var client = factory.CreateClient();
        client.Timeout = _defaultTestClientTimeout;
        if (_useSharedServer && !string.IsNullOrWhiteSpace(_currentSchema))
        {
            client.DefaultRequestHeaders.Add("X-Honua-Test-Schema", _currentSchema);
        }
        configure?.Invoke(client);
        return client;
    }

    /// <summary>
    /// Create a new HTTP client with admin authorization for testing admin endpoints.
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        return CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add("X-API-Key", SharedAdminPassword);
        });
    }

    /// <summary>
    /// Create a new HTTP client scoped to a specific database schema.
    /// </summary>
    public HttpClient CreateClient(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            _currentSchema = schemaName;
        }

        var client = CreateClient();
        if (_useSharedServer && !string.IsNullOrWhiteSpace(schemaName))
        {
            client.DefaultRequestHeaders.Remove("X-Honua-Test-Schema");
            client.DefaultRequestHeaders.Add("X-Honua-Test-Schema", schemaName);
        }

        return client;
    }
}
