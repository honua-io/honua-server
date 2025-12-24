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
    private readonly PostgresFixture _postgres;
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private WebApplicationFactory<Program>? _factory;
    private string? _currentSchema;

    public WebAppFixture()
    {
        _postgres = new PostgresFixture();
    }

    public HttpClient Client { get; private set; } = null!;

    public PostgresFixture Postgres => _postgres;

    public string? CurrentSchema => _currentSchema;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Configure test environment
                builder.UseEnvironment("Test");

                // Configure authentication bypass for test environment
                builder.UseSetting("HONUA_DEV_AUTH", "true");

                // Configure application configuration with test connection string BEFORE app startup
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:honua"] = _postgres.ConnectionString
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

                    // Register NpgsqlDataSource with test connection string
                    services.AddSingleton<NpgsqlDataSource>(_ =>
                    {
                        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.ConnectionString);
                        return dataSourceBuilder.Build();
                    });

                    // Register all PostgreSQL services manually with test implementations
                    services.AddScoped<IFeatureStore, Honua.Postgres.Features.FeatureStore.PostgresFeatureStore>();
                    services.AddScoped<ISqlFilterTranslator>(_ => new Honua.Postgres.Queries.Filters.PostgresSqlFilterTranslator(
                        useJsonAttributes: true,
                        attributesColumn: "attributes",
                        geometryColumn: "geometry",
                        primaryKeyColumn: "objectid"));
                    services.AddScoped<IAttachmentStore, Honua.Postgres.Features.Attachments.PostgresAttachmentStore>();
                    services.AddScoped<ILayerCatalog, Honua.Postgres.Features.Catalog.PostgresLayerCatalogForCite>();
                    services.AddScoped<ITableDiscoveryService, Honua.Postgres.Features.Admin.PostgreSqlTableDiscoveryService>();
                    services.AddScoped<IDatabaseHealthChecker, Honua.Postgres.Features.HealthCheck.PostgresDatabaseHealthChecker>();
                    services.AddScoped<IDatabaseConnectionProvider>(serviceProvider =>
                    {
                        var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
                        return new TestDatabaseConnectionProvider(dataSource);
                    });
                    services.AddScoped<ICrsDetectionService>(_ => new Honua.Postgres.Features.Import.CrsDetectionService(_postgres.ConnectionString));
                    services.AddScoped<IFileImportService>(serviceProvider =>
                    {
                        var crsDetectionService = serviceProvider.GetRequiredService<ICrsDetectionService>();
                        return new Honua.Postgres.Features.Import.FileImportService(_postgres.ConnectionString, crsDetectionService);
                    });

                    // Apply custom service configurations
                    foreach (var configure in _serviceConfigurations)
                    {
                        configure(services);
                    }
                });
            });

        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (_currentSchema is not null)
        {
            await _postgres.DropSchemaAsync(_currentSchema);
        }

        Client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
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
        return _factory!.Services.GetRequiredService<T>();
    }

    /// <summary>
    /// Get an optional service from the test server's DI container.
    /// </summary>
    public T? GetOptionalService<T>() where T : class
    {
        return _factory!.Services.GetService<T>();
    }

    /// <summary>
    /// Create an isolated schema for this test.
    /// Schema is automatically cleaned up on dispose.
    /// </summary>
    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        _currentSchema = await _postgres.CreateIsolatedSchemaAsync(testClassName);
        return _currentSchema;
    }

    /// <summary>
    /// Reset database state in the public schema (legacy method).
    /// Prefer schema-based isolation for parallel execution.
    /// </summary>
    public async Task ResetAsync()
    {
        await _postgres.ResetAsync();
    }

    /// <summary>
    /// Create a new HTTP client with custom configuration.
    /// </summary>
    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        var client = _factory!.CreateClient();
        configure?.Invoke(client);
        return client;
    }
}
