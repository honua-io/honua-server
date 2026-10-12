// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Protocols.Ogc.Api.Maps;
using Honua.TestKit;
using Honua.TestKit.Hosting;
using Honua.TestKit.Postgres.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>Production OGC Maps routes over the canonical Postgres component services.</summary>
public sealed class OgcMapsComponentFixture : IAsyncLifetime
{
    private PostgresFixture? _postgres;
    private ComponentTestHost? _host;
    private string? _connection;

    public HttpClient Client => _host?.Client ?? throw new InvalidOperationException("OGC Maps fixture is not initialized.");

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgresFixture(boundedResources: true);
            await _postgres.InitializeAsync();
            var connection = await _postgres.ProvisionDatabaseAsync(RepositoryPaths.Resolve("tests", "seed", "odata.yaml"));
            _connection = connection;
            _host = await ComponentTestHost.StartAsync(builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HONUA_ADMIN_PASSWORD"] = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                    ["Security:ConnectionEncryption:MasterKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                });
                PostgresComponentServices.Configure(builder, connection);
                builder.Services.AddOgcMaps();
            }, app =>
            {
                PostgresComponentServices.UseRequestPipeline(app);
                app.MapOgcMapsEndpoints();
            });
        }
        catch (Exception initializationError)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("OGC Maps fixture initialization and cleanup failed.", initializationError, cleanupError);
            }
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        var host = _host;
        var postgres = _postgres;
        var connection = _connection;
        _host = null;
        _postgres = null;
        _connection = null;
        try
        {
            if (host is not null) await host.DisposeAsync();
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

[CollectionDefinition("Database.OgcApiTiles")]
public sealed class OgcMapsComponentCollection;
