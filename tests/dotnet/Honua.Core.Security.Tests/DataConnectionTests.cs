// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Core.Security.Tests;

/// <summary>
/// Tests for <see cref="DataConnection"/> verifying provider-name normalization
/// and that encrypted-credential construction enforces strict SSL/TLS transport
/// modes when <see cref="DataConnection.SslRequired"/> is set.
/// </summary>
public sealed class DataConnectionTests
{
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
}
