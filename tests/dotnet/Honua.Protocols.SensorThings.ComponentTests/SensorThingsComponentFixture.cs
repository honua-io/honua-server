// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Protocols.SensorThings;
using Honua.TestKit.Hosting;
using Honua.TestKit.Postgres.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.SensorThings.ComponentTests;

public sealed class SensorThingsComponentFixture : IAsyncLifetime, IAsyncDisposable
{
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    private readonly PostgresComponentDatabase _database = new();
    private ComponentTestHost? _host;
    public HttpClient Client => _host?.Client ?? throw new InvalidOperationException("Host not started.");

    public async Task InitializeAsync()
    {
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests/seed/server.yaml")))
                directory = directory.Parent;
            var seed = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Canonical server seed not found."), "tests/seed/server.yaml");
            await _database.InitializeAsync(seed);
            var key = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _host = await ComponentTestHost.StartAsync(builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HONUA_ADMIN_PASSWORD"] = key,
                    ["Security:ConnectionEncryption:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                });
                PostgresComponentServices.Configure(builder, _database.ConnectionString);
                builder.Services.AddSensorThings();
            }, app =>
            {
                PostgresComponentServices.UseRequestPipeline(app);
                app.MapSensorThingsEndpoints();
            });
            Client.DefaultRequestHeaders.Add("X-API-Key", key);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public Task DisposeAsync()
    {
        lock (_disposeGate)
        {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
                _host = null;
            }
        }
        finally
        {
            await _database.DisposeAsync();
        }
    }
}
