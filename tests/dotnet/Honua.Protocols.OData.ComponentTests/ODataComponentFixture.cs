// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.OData.TestHost;
using Honua.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Honua.Protocols.OData.ComponentTests;

public sealed class ODataComponentFixture : IAsyncLifetime, IAsyncDisposable
{
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    private PostgresFixture? _postgres;
    private WebApplication? _app;
    private string? _connection;
    public HttpClient Client { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;
    public bool IsExternal { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        catch (Exception initializationError)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("OData fixture initialization and cleanup failed.", initializationError, cleanupError);
            }
            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        var external = Environment.GetEnvironmentVariable("HONUA_ODATA_TEST_BASE_URL");
        string adminKey;
        if (!string.IsNullOrWhiteSpace(external))
        {
            IsExternal = true;
            Client = new HttpClient { BaseAddress = new Uri(external), Timeout = TimeSpan.FromSeconds(30) };
            Anonymous = new HttpClient { BaseAddress = new Uri(external), Timeout = TimeSpan.FromSeconds(30) };
            adminKey = Environment.GetEnvironmentVariable("HONUA_ODATA_TEST_ADMIN_KEY")
                ?? throw new InvalidOperationException("External parity requires HONUA_ODATA_TEST_ADMIN_KEY.");
        }
        else
        {
            _postgres = new PostgresFixture(boundedResources: true);
            await _postgres.InitializeAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests/seed/odata.yaml")))
                directory = directory.Parent;
            var seed = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Canonical OData seed not found."), "tests/seed/odata.yaml");
            _connection = await _postgres.ProvisionODataDatabaseAsync(seed);
            adminKey = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _app = ODataTestApplication.Build([], builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _connection,
                    ["HONUA_ADMIN_PASSWORD"] = adminKey,
                    ["Security:ConnectionEncryption:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    ["OData:MaxPageSize"] = "2",
                    ["Limits:Edits:MaxPayloadSize"] = "4096"
                });
            });
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _app.StartAsync(startupTimeout.Token);
            Client = _app.GetTestClient();
            Anonymous = _app.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(30);
            Anonymous.Timeout = TimeSpan.FromSeconds(30);
        }
        Client.DefaultRequestHeaders.Add("X-API-Key", adminKey);
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
        Client?.Dispose();
        Anonymous?.Dispose();
        // Each owned resource gets a cleanup attempt even when an earlier cleanup fails.
        var app = _app;
        var postgres = _postgres;
        var connection = _connection;
        _app = null;
        _postgres = null;
        _connection = null;
        try
        {
            if (app is not null) await app.DisposeAsync();
        }
        finally
        {
            if (postgres is not null)
            {
                try
                {
                    if (connection is not null)
                        await postgres.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(connection).Database!);
                }
                finally
                {
                    await postgres.DisposeAsync();
                }
            }
        }
    }
}
