// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Configurations;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.MsSql;
using Xunit;

namespace Honua.TestKit.Providers;

public sealed class SqlServerProviderWebAppFixture : IAsyncLifetime
{
    private MsSqlContainer _container = null!;
    private WebAppFixture _webApp = null!;
    private string _tableName = null!;
    private X509Certificate2? _trustedRoot;

    public HttpClient Client => _webApp.Client;

    public WebAppFixture WebApp => _webApp;

    public TestMetadataV2GraphProvider GraphProvider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            var (certificatePem, privateKeyPem) = CreateTrustedServerCertificate();
            // mssql runs as uid10001. Keep the ephemeral private key readable only by that user.
            // https://learn.microsoft.com/en-us/sql/linux/containers/security
            _container = new MsSqlBuilder()
                .WithResourceMapping(Encoding.ASCII.GetBytes(certificatePem), "/var/opt/mssql/test-server.pem", 10001, 0,
                    UnixFileModes.UserRead | UnixFileModes.UserWrite)
                .WithResourceMapping(Encoding.ASCII.GetBytes(privateKeyPem), "/var/opt/mssql/test-server.key", 10001, 0,
                    UnixFileModes.UserRead | UnixFileModes.UserWrite)
                .WithResourceMapping(Encoding.ASCII.GetBytes("""
                    [network]
                    tlscert = /var/opt/mssql/test-server.pem
                    tlskey = /var/opt/mssql/test-server.key
                    forceencryption = 1
                    """), "/var/opt/mssql/mssql.conf")
                .Build();
            await _container.StartAsync().ConfigureAwait(false);

            _tableName = $"parcels_{Guid.NewGuid():N}";
            await SeedAsync().ConfigureAwait(false);

            var connectionId = Guid.NewGuid();
            var graph = ProviderSmokeGraph.Build(
                locator: $"dbo.{_tableName}",
                connectionId: connectionId.ToString(),
                connectionProvider: "sqlserver");

            _webApp = new WebAppFixture();
            _webApp.ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                GraphProvider = new TestMetadataV2GraphProvider(graph);
                services.AddSingleton(GraphProvider);
                services.AddSingleton<IMetadataV2GraphProvider>(sp => sp.GetRequiredService<TestMetadataV2GraphProvider>());
                services.AddSingleton<IMetadataV2GraphStore>(sp => sp.GetRequiredService<TestMetadataV2GraphProvider>());

                // WebAppFixture's isolated test host bypasses InfrastructureCompositionRoot
                // entirely (Program.cs's TestInfrastructureRegistrationPolicy skips it in the
                // Test environment so WebAppFixture can wire its own providers), which is
                // exactly where AddSqlServerFeatureProvider normally gets called in production.
                // WebAppFixturePostgresWiringMixin only re-registers the Postgres primary, so
                // SQL Server must be registered explicitly here (honua-server#2947).
                Honua.Db.SqlServer.ServiceCollectionExtensions.AddSqlServerFeatureProvider(
                    services, new ConfigurationBuilder().Build());
            });

            await _webApp.InitializeAsync().ConfigureAwait(false);

            await RegisterSecureConnectionAsync(connectionId).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private (string CertificatePem, string PrivateKeyPem) CreateTrustedServerCertificate()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest($"CN=Honua SQL test {Guid.NewGuid():N}", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        var dockerHost = Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE");
        if (string.IsNullOrWhiteSpace(dockerHost) && Uri.TryCreate(Environment.GetEnvironmentVariable("DOCKER_HOST"), UriKind.Absolute, out var endpoint) && (endpoint.Scheme is "tcp" or "http" or "https"))
        {
            dockerHost = endpoint.Host;
        }
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            if (IPAddress.TryParse(dockerHost, out var address))
            {
                names.AddIpAddress(address);
            }
            else
            {
                names.AddDnsName(dockerHost);
            }
        }
        request.CertificateExtensions.Add(names.Build());
        using var server = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        _trustedRoot = X509CertificateLoader.LoadCertificate(root.RawData);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(_trustedRoot);
        return (server.ExportCertificatePem(), serverKey.ExportPkcs8PrivateKeyPem());
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_webApp is not null)
            {
                await _webApp.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (_container is not null)
                {
                    await _container.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (_trustedRoot is not null)
                {
                    using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Remove(_trustedRoot);
                    _trustedRoot.Dispose();
                    _trustedRoot = null;
                }
            }
        }
    }

    private async Task SeedAsync()
    {
        var secured = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Encrypt = true,
            TrustServerCertificate = false
        };
        await using var connection = new SqlConnection(secured.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await ExecuteAsync(connection, $"""
            CREATE TABLE [dbo].[{_tableName}] (
                [objectid] bigint NOT NULL PRIMARY KEY,
                [geom] geometry NULL,
                [name] nvarchar(64) NULL,
                [area] float NULL,
                [type] nvarchar(32) NULL
            )
            """).ConfigureAwait(false);

        foreach (var parcel in ProviderSmokeData.Parcels)
        {
            await ExecuteAsync(connection, FormattableString.Invariant($"""
                INSERT INTO [dbo].[{_tableName}] ([objectid], [geom], [name], [area], [type])
                VALUES ({parcel.Id}, geometry::STGeomFromText('POINT({parcel.Longitude} {parcel.Latitude})', 4326), '{parcel.Name}', {parcel.Area}, '{parcel.Type}')
                """)).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task RegisterSecureConnectionAsync(Guid connectionId)
    {
        var registry = _webApp.GetService<ISecureConnectionRegistry>();
        var encryption = _webApp.GetService<IConnectionEncryptionService>();

        // Exercise the production resolver and real TLS validation against our isolated CA.
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Encrypt = true,
            TrustServerCertificate = false,
        };
        var rawConnectionString = builder.ConnectionString;

        var encrypted = await encryption.EncryptConnectionStringAsync(rawConnectionString).ConfigureAwait(false);
        var keyVersion = await encryption.GetCurrentKeyVersionAsync().ConfigureAwait(false);

        var hostPort = builder.DataSource.Split(',', 2);
        var host = hostPort[0];
        var port = hostPort.Length > 1 && int.TryParse(hostPort[1], out var parsedPort) ? parsedPort : 1433;

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"sqlserver-smoke-{connectionId:N}",
            host: host,
            port: port,
            databaseName: string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "master" : builder.InitialCatalog,
            username: builder.UserID,
            encryptedConnectionString: encrypted,
            encryptionKeyVersion: keyVersion,
            createdBy: "provider-smoke-fixture",
            sslRequired: true,
            sslMode: SslMode.VerifyFull);
        connection.ConnectionId = connectionId;
        connection.Provider = "sqlserver";

        await registry.CreateConnectionAsync(connection).ConfigureAwait(false);
    }
}
