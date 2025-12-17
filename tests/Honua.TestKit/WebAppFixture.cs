// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.TestKit;

/// <summary>
/// Web application fixture for integration tests.
/// Combines WebApplicationFactory with PostgresFixture.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;

    public WebAppFixture()
    {
        _postgres = new PostgresFixture();
    }

    public HttpClient Client { get; private set; } = null!;

    public PostgresFixture Postgres => _postgres;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Replace connection string with test container
                    services.AddNpgsqlDataSource(_postgres.ConnectionString);
                });
            });

        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Get a service from the test server's DI container.
    /// </summary>
    public T GetService<T>() where T : notnull
    {
        return _factory!.Services.GetRequiredService<T>();
    }

    /// <summary>
    /// Reset database state between tests.
    /// </summary>
    public async Task ResetAsync()
    {
        await _postgres.ResetAsync();
    }
}
