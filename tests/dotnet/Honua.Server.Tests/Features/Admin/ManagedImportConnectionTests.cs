// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ManagedImportConnectionTests
{
    private const string Password = "secret-value";
    private const string ConnectionString =
        "Host=db.internal;Port=5432;Database=honua;Username=honua_app;Password=" + Password + ";SSL Mode=Disable";

    [UnitTest]
    public async Task FreshInstallListsOneImportedDataConnectionWithoutThePassword()
    {
        var encryption = new RememberingEncryption();
        using var services = Services(encryption, ConnectionString);
        var registry = services.GetRequiredService<ISecureConnectionRegistry>();

        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);
        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);

        var connection = Assert.Single(await registry.GetActiveConnectionsAsync());
        Assert.Equal(ManagedImportConnection.Id, connection.ConnectionId);
        Assert.Equal(ManagedImportConnection.Name, connection.Name);
        Assert.Equal("db.internal", connection.Host);
        Assert.Equal(5432, connection.Port);
        Assert.Equal("honua", connection.DatabaseName);
        Assert.Equal("honua_app", connection.Username);
        Assert.Equal(SslMode.Disable, connection.SslMode);
        Assert.False(connection.SslRequired);
        Assert.Equal(ConnectionString, encryption.Stored);
        Assert.Equal(1, encryption.EncryptCalls);
        Assert.DoesNotContain(Password, connection.Host + connection.DatabaseName + connection.Username + connection.Description);
    }

    [UnitTest]
    public async Task ChangedServerDatabaseReplacesTheStoredConnectionString()
    {
        var encryption = new RememberingEncryption();
        using var services = Services(encryption, ConnectionString);
        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);
        var moved = ConnectionString.Replace("db.internal", "db.moved", StringComparison.Ordinal);
        using var relocated = Services(encryption, moved, services.GetRequiredService<ISecureConnectionRegistry>());

        await ManagedImportConnection.EnsureAsync(relocated, CancellationToken.None);

        var connection = Assert.Single(await relocated.GetRequiredService<ISecureConnectionRegistry>().GetActiveConnectionsAsync());
        Assert.Equal("db.moved", connection.Host);
        Assert.Equal(moved, encryption.Stored);
        Assert.Equal(2, encryption.EncryptCalls);
    }

    [UnitTest]
    public async Task MissingOrUnreadableServerDatabaseAddsNothing()
    {
        var encryption = new RememberingEncryption();
        using var missing = Services(encryption, null);
        using var broken = Services(encryption, "not a connection string");

        await ManagedImportConnection.EnsureAsync(missing, CancellationToken.None);
        await ManagedImportConnection.EnsureAsync(broken, CancellationToken.None);

        Assert.Empty(await missing.GetRequiredService<ISecureConnectionRegistry>().GetAllConnectionsAsync());
        Assert.Empty(await broken.GetRequiredService<ISecureConnectionRegistry>().GetAllConnectionsAsync());
        Assert.Equal(0, encryption.EncryptCalls);
    }

    [UnitTest]
    public async Task NameAlreadyUsedForThisDatabaseAddsNothing()
    {
        var encryption = new RememberingEncryption();
        var registry = new InMemorySecureConnectionRegistry();
        await registry.CreateConnectionAsync(Seed("11111111-1111-4111-8111-111111111111", ManagedImportConnection.Name, ConnectionString));
        using var services = Services(encryption, ConnectionString, registry);

        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);

        Assert.Single(await registry.GetAllConnectionsAsync());
        Assert.Equal(0, encryption.EncryptCalls);
    }

    [UnitTest]
    public async Task NameAlreadyUsedForAnotherDatabaseUsesTheBuiltInName()
    {
        var encryption = new RememberingEncryption();
        var registry = new InMemorySecureConnectionRegistry();
        await registry.CreateConnectionAsync(Seed(
            "22222222-2222-4222-8222-222222222222",
            ManagedImportConnection.Name,
            "Host=elsewhere;Port=5432;Database=other;Username=other;Password=other"));
        using var services = Services(encryption, ConnectionString, registry);

        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);

        var created = Assert.Single(await registry.GetActiveConnectionsAsync(), connection => connection.ConnectionId == ManagedImportConnection.Id);
        Assert.Equal(ManagedImportConnection.FallbackName, created.Name);
        Assert.Equal(2, (await registry.GetAllConnectionsAsync()).Count());
    }

    [UnitTest]
    public async Task BuiltInNameAlreadyUsedForThisDatabaseAddsNothing()
    {
        var encryption = new RememberingEncryption();
        var registry = new InMemorySecureConnectionRegistry();
        await registry.CreateConnectionAsync(Seed(
            "33333333-3333-4333-8333-333333333333",
            ManagedImportConnection.FallbackName,
            ConnectionString));
        using var services = Services(encryption, ConnectionString, registry);

        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);

        Assert.Single(await registry.GetAllConnectionsAsync());
        Assert.Equal(0, encryption.EncryptCalls);
    }

    [UnitTest]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The service provider disposes the registered data source.")]
    public async Task RunningDatabaseWinsOverAReloadedConfiguration()
    {
        var encryption = new RememberingEncryption();
        var running = ConnectionString.Replace("db.internal", "db.running", StringComparison.Ordinal);
        var reloaded = ConnectionString.Replace("db.internal", "db.reloaded", StringComparison.Ordinal);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(running) { PersistSecurityInfo = true };
#pragma warning disable CA2000 // The service provider disposes the registered data source.
        var dataSource = Npgsql.NpgsqlDataSource.Create(builder.ConnectionString);
#pragma warning restore CA2000
        using var services = Services(encryption, reloaded, dataSource: dataSource);

        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);
        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);

        var connection = Assert.Single(await services.GetRequiredService<ISecureConnectionRegistry>().GetActiveConnectionsAsync());
        Assert.Equal("db.running", connection.Host);
        Assert.Contains("db.running", encryption.Stored, StringComparison.Ordinal);
        Assert.DoesNotContain("db.reloaded", encryption.Stored, StringComparison.Ordinal);
        Assert.Equal(1, encryption.EncryptCalls);
    }

    [UnitTest]
    public async Task EncryptionFailureLeavesTheListUnchanged()
    {
        var registry = new InMemorySecureConnectionRegistry();
        using var services = Services(new RememberingEncryption { Fail = true }, ConnectionString, registry);

        await ManagedImportConnection.EnsureAsync(services, CancellationToken.None);

        Assert.Empty(await registry.GetAllConnectionsAsync());
    }

    private static ServiceProvider Services(
        RememberingEncryption encryption,
        string? connectionString,
        ISecureConnectionRegistry? registry = null,
        Npgsql.NpgsqlDataSource? dataSource = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IConnectionEncryptionService>(encryption)
            .AddSingleton(registry ?? new InMemorySecureConnectionRegistry());
        if (dataSource is not null)
        {
            services.AddSingleton(dataSource);
        }

        return services.BuildServiceProvider();
    }

    private sealed class RememberingEncryption : IConnectionEncryptionService
    {
        public string? Stored { get; private set; }
        public int EncryptCalls { get; private set; }
        public bool Fail { get; init; }

        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
        {
            if (Fail)
            {
                throw new InvalidOperationException("The encryption key is not available.");
            }

            EncryptCalls++;
            Stored = connectionString;
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(connectionString));
        }

        public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
            => Task.FromResult(System.Text.Encoding.UTF8.GetString(encryptedData));

        public Task<int> GetCurrentKeyVersionAsync() => Task.FromResult(1);
        public Task<bool> ValidateEncryptionAsync() => Task.FromResult(true);
        public Task<int> RotateKeyAsync() => Task.FromResult(2);
    }

    private static DataConnection Seed(string id, string name, string connectionString)
    {
        var connectionId = Guid.Parse(id);
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name,
            "elsewhere",
            5432,
            "other",
            "other",
            System.Text.Encoding.UTF8.GetBytes(connectionString),
            1,
            "operator",
            "operator connection",
            sslRequired: false,
            SslMode.Disable);
        connection.ConnectionId = connectionId;
        connection.Id = connectionId.ToString("D");
        return connection;
    }
}
