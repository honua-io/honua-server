// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using System.Text.Json;
using Xunit;

namespace Honua.Core.Security.Tests;

/// <summary>
/// Tests for <see cref="DataConnection"/> verifying provider-name normalization
/// and that encrypted-credential construction enforces strict SSL/TLS transport
/// modes when <see cref="DataConnection.SslRequired"/> is set.
/// </summary>
public sealed class DataConnectionTests
{
    [Trait("Tier", "Fast")]
    [SecurityTest]
    [Theory]
    [InlineData(null, "environment")]
    [InlineData("env:TEST_CONNECTION", null)]
    [InlineData("", "environment")]
    [InlineData("env:TEST_CONNECTION", "")]
    public void Validate_UndeclaredPortWithoutCompleteSecretMetadata_IsInvalid(string? reference, string? secretType)
    {
        var connection = new DataConnection
        {
            Name = "test",
            Host = "localhost",
            Port = 0,
            DatabaseName = "test",
            Username = "test",
            IsEncrypted = false,
            SecretRef = reference,
            SecretType = secretType
        };

        Assert.False(connection.Validate());
    }

    [Trait("Tier", "Fast")]
    [SecurityTest]
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 5432, true)]
    [InlineData(true, 6432, true)]
    [InlineData(false, -1, false)]
    [InlineData(true, -1, false)]
    [InlineData(false, 65536, false)]
    [InlineData(true, 65536, false)]
    public void Validate_PortUndeclared_OnlyAcceptedForExternalSecret(bool external, int port, bool valid)
    {
        var connection = external
            ? DataConnection.CreateWithSecretReference("test", "localhost", port, "test", "test",
                "env:TEST_CONNECTION", "environment", "test", SslMode.Require)
            : DataConnection.CreateWithEncryptedCredentials("test", "localhost", port, "test", "test",
                [1, 2, 3], 1, "test", sslRequired: true, sslMode: SslMode.Require);

        Assert.Equal(valid, connection.Validate());
    }

    [Trait("Tier", "Fast")]
    [SecurityTest]
    [Theory]
    [InlineData(null, "postgis")]
    [InlineData("", "postgis")]
    [InlineData("Postgres", "postgis")]
    [InlineData("PostgreSQL", "postgresql")]
    [InlineData("sql_server", "sqlserver")]
    [InlineData("MariaDB", "mysql")]
    public void NormalizedProvider_ReturnsCanonicalProviderName(string? provider, string expected)
    {
        var connection = new DataConnection { Provider = provider ?? string.Empty };

        Assert.Equal(expected, connection.NormalizedProvider);
    }

    [Trait("Tier", "Fast")]
    [SecurityTest]
    [Theory]
    [InlineData(SslMode.Allow)]
    [InlineData(SslMode.Prefer)]
    public void CreateWithEncryptedCredentials_SslRequiredWithFallbackMode_ThrowsInvalidOperationException(SslMode sslMode)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataConnection.CreateWithEncryptedCredentials(
                name: "analytics",
                host: "db.example.com",
                port: 5432,
                databaseName: "analytics",
                username: "app",
                encryptedConnectionString: [1, 2, 3],
                encryptionKeyVersion: 1,
                createdBy: "test",
                sslRequired: true,
                sslMode: sslMode));

        Assert.Equal("SSL mode must require encrypted transport when SslRequired is true", exception.Message);
    }

    [Trait("Tier", "Fast")]
    [SecurityTest]
    [Theory]
    [InlineData(SslMode.Require)]
    [InlineData(SslMode.VerifyCa)]
    [InlineData(SslMode.VerifyFull)]
    public void CreateWithEncryptedCredentials_SslRequiredWithStrictMode_PreservesSslMode(SslMode sslMode)
    {
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: "analytics",
            host: "db.example.com",
            port: 5432,
            databaseName: "analytics",
            username: "app",
            encryptedConnectionString: [1, 2, 3],
            encryptionKeyVersion: 1,
            createdBy: "test",
            sslRequired: true,
            sslMode: sslMode);

        Assert.Equal(sslMode, connection.SslMode);
        Assert.Equal("postgis", connection.NormalizedProvider);
    }

    [Trait("Tier", "Fast")]
    [SecurityTest]
    [Fact]
    public void Serialize_DoesNotEmitRawConnectionString()
    {
        var connection = new DataConnection
        {
            Name = "analytics",
            ConnectionString = "Host=db.example.com;Username=app;Password=secret",
            SecretRef = "vault://connections/analytics",
            Properties = new Dictionary<string, object>
            {
                ["token"] = "secret-token"
            }
        };

        var json = JsonSerializer.Serialize(connection);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty(nameof(DataConnection.ConnectionString), out _));
        Assert.True(document.RootElement.TryGetProperty(nameof(DataConnection.EncryptedConnectionString), out _));
        Assert.DoesNotContain("Password=secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SecretRef", json, StringComparison.OrdinalIgnoreCase);
    }
}
