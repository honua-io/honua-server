// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
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

namespace Honua.TestKit;

/// <summary>
/// Web application fixture for integration tests.
/// Combines WebApplicationFactory with PostgresFixture.
/// Supports service replacement and schema-based isolation.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
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

    public WebAppFixture()
    {
    }

    public HttpClient Client { get; private set; } = null!;

    public PostgresFixture Postgres => _useSharedServer
        ? _sharedPostgres ?? throw new InvalidOperationException("Shared Postgres fixture not initialized.")
        : _postgres ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public PostgresFixture PostgresFixture => Postgres;

    public string? CurrentSchema => _currentSchema;

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

                // Configure authentication bypass for test environment
                builder.UseSetting("HONUA_DEV_AUTH", "true");

                _configureWebHost?.Invoke(builder);

                // Configure application configuration with test connection string BEFORE app startup
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:honua"] = _postgres.ConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Remove and re-register all PostgreSQL services with test connection string
                    services.RemoveAll<NpgsqlDataSource>();
                    services.RemoveAll<IFeatureStore>();
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
                            ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString
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

        Client = _factory.CreateClient();

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await _postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
            await SeedSchemaAsync(_currentSchema);
        }
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
                        builder.UseEnvironment("Development");
                        builder.UseSetting("HONUA_DEV_AUTH", "true");

                        builder.ConfigureAppConfiguration((context, configBuilder) =>
                        {
                            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:honua"] = _sharedPostgres.ConnectionString,
                                ["ConnectionStrings:DefaultConnection"] = _sharedPostgres.ConnectionString,
                                ["HONUA_DEV_AUTH"] = "true",
                                ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
                                ["Database:QueryCache:EnableAutomaticCaching"] = "false",
                                ["Attachments:StoragePath"] = attachmentsPath
                            });
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

        Client = CreateClient(_currentSchema);
    }

    public async Task DisposeAsync()
    {
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
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        return factory.Services.GetRequiredService<T>();
    }

    /// <summary>
    /// Get an optional service from the test server's DI container.
    /// </summary>
    public T? GetOptionalService<T>() where T : class
    {
        var factory = _useSharedServer ? _sharedFactory : _factory;
        return factory?.Services.GetService<T>();
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
        return _currentSchema;
    }

    private Task SeedSchemaAsync(string schemaName)
    {
        if (!_useSharedServer && string.IsNullOrWhiteSpace(_seedPath))
        {
            return Task.CompletedTask;
        }

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
    /// Create a new HTTP client with custom configuration.
    /// </summary>
    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        var client = factory.CreateClient();
        if (_useSharedServer && !string.IsNullOrWhiteSpace(_currentSchema))
        {
            client.DefaultRequestHeaders.Add("X-Honua-Test-Schema", _currentSchema);
        }
        configure?.Invoke(client);
        return client;
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
