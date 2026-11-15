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

    private static ServiceProvider Services(RememberingEncryption encryption, string? connectionString, ISecureConnectionRegistry? registry = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString
        }).Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IConnectionEncryptionService>(encryption)
            .AddSingleton(registry ?? new InMemorySecureConnectionRegistry())
            .BuildServiceProvider();
    }

    private sealed class RememberingEncryption : IConnectionEncryptionService
    {
        public string? Stored { get; private set; }
        public int EncryptCalls { get; private set; }

        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
        {
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
}
