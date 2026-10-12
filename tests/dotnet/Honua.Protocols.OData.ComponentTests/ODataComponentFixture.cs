// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.OData.TestHost;
using Honua.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Honua.Protocols.OData.ComponentTests;

public sealed class ODataComponentFixture : IAsyncLifetime
{
    private PostgresFixture? _postgres;
    private WebApplication? _app;
    private string? _connection;
    public HttpClient Client { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;
    public bool IsExternal { get; private set; }

    public async Task InitializeAsync()
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
            adminKey = "OData-Pilot-Admin-1a";
            _app = ODataTestApplication.Build([], builder =>
            {
                builder.WebHost.UseEnvironment("Production").UseTestServer();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _connection,
                    ["HONUA_ADMIN_PASSWORD"] = adminKey,
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

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        Anonymous?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (_postgres is not null)
        {
            if (_connection is not null)
                await _postgres.DropDatabaseAsync(new NpgsqlConnectionStringBuilder(_connection).Database!);
            await _postgres.DisposeAsync();
        }
    }
}
