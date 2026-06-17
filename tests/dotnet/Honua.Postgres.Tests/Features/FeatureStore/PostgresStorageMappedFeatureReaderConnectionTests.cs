// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Connection-resolution behavior for the storage-mapped reader. Regression coverage for
/// the single-DB / default-connection deployment (demo.honua.io): a layer bound to an
/// empty/placeholder data connection must fall back to the default database connection
/// (the same source render + OGC reads use) instead of failing the FeatureServer query,
/// while a layer bound to a real (decryptable) connection still uses that source, and a
/// genuinely unresolvable binding still fails loudly.
/// </summary>
public sealed class PostgresStorageMappedFeatureReaderConnectionTests
{
    private static readonly ObjectPool<Dictionary<string, object?>> DictionaryPool =
        new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());

    [Fact]
    public async Task ResolveBoundConnectionString_EmptyOrDefaultBinding_FallsBackToDefaultConnection()
    {
        // A placeholder connection that carries no connection-string material at all
        // (no plaintext, no encrypted bytes, no secret reference). This is the common
        // single-DB case: the layer is bound to '' / the default connection.
        var connection = new DataConnection
        {
            Id = string.Empty,
            IsEncrypted = false,
            ConnectionString = string.Empty,
        };

        var resolved = await ResolveBoundConnectionStringAsync(
            connection,
            connectionEncryptionService: null);

        // null is the "use the default connection provider" signal honored by
        // OpenConnectionAsync, restoring the pre-#1662 behavior.
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveBoundConnectionString_EmptyIdWithUndecryptableEncryptedBytes_FallsBackToDefaultConnection()
    {
        // The exact demo.honua.io situation: the maui layers are bound to a connection whose
        // ID is empty ('' — the default-connection sentinel) but which still carries stale
        // encrypted credentials that won't decrypt with the current master key. An empty ID
        // means "use the default connection", so it must fall back regardless of the leftover
        // (and unusable) encrypted bytes rather than failing the FeatureServer / OGC query.
        var connection = new DataConnection
        {
            Id = string.Empty,
            IsEncrypted = true,
            EncryptedConnectionString = Encoding.UTF8.GetBytes("stale-cipher-text"),
            EncryptionKeyVersion = 1,
        };

        // Encryption service present but unable to unlock the stale bytes (wrong master key).
        var encryptionService = Substitute.For<IConnectionEncryptionService>();
        encryptionService
            .DecryptConnectionStringAsync(Arg.Any<byte[]>(), Arg.Any<int>())
            .Returns(Task.FromResult(string.Empty));

        var resolved = await ResolveBoundConnectionStringAsync(connection, encryptionService);

        // null is the "use the default connection provider" signal honored by
        // OpenConnectionAsync, restoring the pre-#1662 behavior for the empty-ID sentinel.
        resolved.Should().BeNull();
    }

    [Fact]
    public void ShouldFailInsteadOfDefaultFallback_EmptyIdWithEncryptedBytes_AllowsFallback()
    {
        // Empty/whitespace ID is the default-connection sentinel — fall back even when stale
        // encrypted bytes linger on the record (the precise demo.honua.io misconfiguration).
        var connection = new DataConnection
        {
            Id = string.Empty,
            IsEncrypted = true,
            EncryptedConnectionString = Encoding.UTF8.GetBytes("stale-cipher-text"),
        };

        PostgresStorageMappedFeatureReader
            .ShouldFailInsteadOfDefaultFallback(connection, connectionEncryptionService: null)
            .Should().BeFalse();
    }

    [Fact]
    public async Task ResolveBoundConnectionString_RealEncryptedConnection_UsesThatSource()
    {
        const string decryptedConnectionString =
            "Host=tenant-db;Port=5432;Database=tenant;Username=svc;Password=secret";
        var encryptedBytes = Encoding.UTF8.GetBytes("cipher-text");

        var connection = new DataConnection
        {
            Id = "tenant-connection",
            IsEncrypted = true,
            EncryptedConnectionString = encryptedBytes,
            EncryptionKeyVersion = 3,
        };

        var encryptionService = Substitute.For<IConnectionEncryptionService>();
        encryptionService
            .DecryptConnectionStringAsync(encryptedBytes, 3)
            .Returns(Task.FromResult(decryptedConnectionString));

        var resolved = await ResolveBoundConnectionStringAsync(connection, encryptionService);

        // The bound, distinct source is honored — it must NOT fall back to the default DB.
        resolved.Should().Be(decryptedConnectionString);
    }

    [Fact]
    public async Task ResolveBoundConnectionString_EncryptedButUndecryptable_ThrowsClearError()
    {
        // Encrypted credentials exist but no encryption service is registered to unlock
        // them. The layer points at a specific, distinct source we cannot reach; silently
        // reading the default database would be a data-source isolation hazard, so fail.
        var connection = new DataConnection
        {
            Id = "encrypted-no-service",
            IsEncrypted = true,
            EncryptedConnectionString = Encoding.UTF8.GetBytes("cipher-text"),
            EncryptionKeyVersion = 1,
        };

        var act = () => ResolveBoundConnectionStringAsync(connection, connectionEncryptionService: null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("encrypted-no-service");
    }

    [Fact]
    public void ShouldFailInsteadOfDefaultFallback_PlaceholderBinding_AllowsFallback()
    {
        var connection = new DataConnection { Id = string.Empty, IsEncrypted = false };

        PostgresStorageMappedFeatureReader
            .ShouldFailInsteadOfDefaultFallback(connection, connectionEncryptionService: null)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldFailInsteadOfDefaultFallback_EncryptedBytesPresent_RequiresFailure()
    {
        var connection = new DataConnection
        {
            Id = "encrypted",
            IsEncrypted = true,
            EncryptedConnectionString = Encoding.UTF8.GetBytes("cipher-text"),
        };

        PostgresStorageMappedFeatureReader
            .ShouldFailInsteadOfDefaultFallback(connection, connectionEncryptionService: null)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldFailInsteadOfDefaultFallback_ExternalSecretReference_RequiresFailure()
    {
        var connection = new DataConnection
        {
            Id = "secret-backed",
            IsEncrypted = false,
            SecretRef = "kv://tenant/db",
            SecretType = "AzureKeyVault",
        };

        PostgresStorageMappedFeatureReader
            .ShouldFailInsteadOfDefaultFallback(connection, connectionEncryptionService: null)
            .Should().BeTrue();
    }

    private static async Task<string?> ResolveBoundConnectionStringAsync(
        DataConnection connection,
        IConnectionEncryptionService? connectionEncryptionService)
    {
        var reader = new PostgresStorageMappedFeatureReader(
            Substitute.For<IAdoNetDatabaseConnectionProvider>(),
            DictionaryPool,
            new MetadataV2Resource(),
            new FeatureStorageMapping(TableName: "maui_features", PrimaryKeyColumn: "objectid"),
            connection,
            connectionEncryptionService);

        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod(
            "ResolveBoundConnectionStringAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        try
        {
            var task = (Task<string?>)method!.Invoke(reader, Array.Empty<object>())!;
            return await task;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real exception (e.g. InvalidOperationException) to the assertion.
            throw ex.InnerException;
        }
    }
}
