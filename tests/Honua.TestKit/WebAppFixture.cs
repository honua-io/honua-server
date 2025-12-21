// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Admin;
using Honua.Postgres.Features.Import;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
                // Configure authentication bypass for test environment
                builder.UseSetting("HONUA_DEV_AUTH", "true");

                builder.ConfigureTestServices(services =>
                {
                    // Remove all PostgreSQL-related services to avoid DefaultConnection dependency
                    services.RemoveAll<NpgsqlDataSource>();
                    services.RemoveAll<IDatabaseConnectionProvider>();
                    services.RemoveAll<IFileImportService>();
                    services.RemoveAll<ITableDiscoveryService>();

                    // Add test-specific PostgreSQL services
                    services.AddSingleton<NpgsqlDataSource>(serviceProvider =>
                        NpgsqlDataSource.Create(_postgres.ConnectionString));

                    services.AddScoped<IDatabaseConnectionProvider>(serviceProvider =>
                    {
                        var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
                        return new TestDatabaseConnectionProvider(dataSource);
                    });

                    // Add test-specific import service with test connection string
                    services.AddScoped<IFileImportService>(serviceProvider =>
                        new FileImportService(_postgres.ConnectionString));

                    // Add test-specific table discovery service
                    services.AddScoped<ITableDiscoveryService, PostgreSqlTableDiscoveryService>();

                    // Apply custom service configurations
                    foreach (var configure in _serviceConfigurations)
                    {
                        configure(services);
                    }
                });

                builder.UseEnvironment("Test");
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
