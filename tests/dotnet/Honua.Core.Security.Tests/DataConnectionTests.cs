using Honua.Core.Features.Security.Domain;
using Xunit;

namespace Honua.Core.Security.Tests;

public sealed class DataConnectionTests
{
    [Theory]
    [InlineData(SslMode.Allow)]
    [InlineData(SslMode.Prefer)]
    public void CreateWithEncryptedCredentialsSslRequiredRejectsFallbackModes(SslMode sslMode)
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

    [Theory]
    [InlineData(SslMode.Require)]
    [InlineData(SslMode.VerifyCa)]
    [InlineData(SslMode.VerifyFull)]
    public void CreateWithEncryptedCredentialsSslRequiredAllowsStrictModes(SslMode sslMode)
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
    }
}
