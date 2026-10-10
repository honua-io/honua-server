// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.MySql.Features.Security;
using Honua.Db.Oracle.Features.Security;
using Honua.Db.Postgres.Features.Security;
using Honua.Db.SqlServer.Features.Security;
using Honua.Snowflake.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Security;

public sealed class ResolvedConnectionPolicyTests
{
    [UnitTheory]
    [InlineData(SslMode.Require)]
    [InlineData(SslMode.VerifyCa)]
    [InlineData(SslMode.VerifyFull)]
    public void SqlServerBuilder_EncryptedModesRequireCertificateValidation(SslMode sslMode)
    {
        var driver = new SqlServerConnectionDriver(NullLogger<SqlServerConnectionDriver>.Instance);
        var value = driver.BuildConnectionString(new ConnectionTarget("db.example.com", 1433, "analytics", "app", "secret", sslMode));
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(value);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.True(driver.InspectConnectionString(value).RequiresTls);
    }

    [UnitTheory]
    [InlineData("mysql", "Server=db.example.com;User ID=app;Password=secret;SSL Mode=Preferred")]
    [InlineData("sqlserver", "Data Source=db.example.com;User ID=app;Password=secret;Encrypt=false")]
    [InlineData("oracle", "Data Source=db.example.com:1521/ORCL;User ID=app;Password=secret")]
    [InlineData("redshift", "Host=db.example.com;Database=analytics;Username=app;Password=secret;SSL Mode=Prefer")]
    [InlineData("snowflake", "account=acme;user=app;password=secret;scheme=http")]
    public async Task RequiredTls_RefusesProviderPlaintextFallback(string provider, string connectionString)
    {
        var resolver = CreateResolver(provider, connectionString, true, "db.example.com");
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveConnectionStringAsync("policy-test"));
    }

    [UnitTheory]
    [InlineData("tcps://db.example.com:1521/ORCL")]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCL)))")]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCL))(SECURITY=(SSL_SERVER_DN_MATCH=NO)))")]
    [InlineData("PRODUCTION_TNS_ALIAS")]
    public void OraclePolicy_RefusesImplicitOrDisabledServerIdentityValidation(string dataSource)
    {
        var driver = new OracleConnectionDriver(NullLogger<OracleConnectionDriver>.Instance);
        Assert.Throws<ArgumentException>(() => driver.InspectConnectionString($"Data Source={dataSource};User ID=app;Password=secret"));
    }

    [UnitTheory]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    [InlineData(@"tcp:(localdb)\MSSQLLocalDB")]
    [InlineData(@"tcp: (localdb)\MSSQLLocalDB")]
    [InlineData(@"(local)\reporting")]
    [InlineData(".")]
    public void SqlServerPolicy_RefusesLocalDbAndPreservesNetworkNamedInstances(string dataSource)
    {
        var driver = new SqlServerConnectionDriver(NullLogger<SqlServerConnectionDriver>.Instance);
        Assert.Throws<ArgumentException>(() => driver.InspectConnectionString($"Data Source={dataSource};Encrypt=true"));
        var network = driver.InspectConnectionString(@"Data Source=db.example.com\reporting;Encrypt=true");
        Assert.Equal(["db.example.com"], network.Hosts);
    }

    [UnitTheory]
    [InlineData("db.example.com,")]
    [InlineData(",db.example.com")]
    [InlineData("db.example.com,,other.example.com")]
    public void MySqlPolicy_RefusesEmptyDestinationEntries(string hosts)
    {
        var driver = new MySqlConnectionDriver(NullLogger<MySqlConnectionDriver>.Instance);
        Assert.Throws<ArgumentException>(() => driver.InspectConnectionString($"Server={hosts};User ID=app;Password=secret;SSL Mode=Required"));
    }

    [UnitTest]
    public async Task SnowflakePolicy_ChecksDriverNormalizedAccountHost()
    {
        const string connectionString = "account=org_account.us-east-1;user=app;password=secret";
        var allowed = CreateResolver("snowflake", connectionString, true, "org-account.us-east-1.snowflakecomputing.com");
        Assert.Equal(connectionString, await allowed.ResolveConnectionStringAsync("policy-test"));
        var refused = CreateResolver("snowflake", connectionString, true, "org_account.us-east-1.snowflakecomputing.com");
        await Assert.ThrowsAsync<InvalidOperationException>(() => refused.ResolveConnectionStringAsync("policy-test"));
    }

    public static IEnumerable<object[]> Cases =>
    [
        ["mysql", "Server=db.example.com;User ID=app;Password=secret;SSL Mode=Required;Connection Timeout=5", "AllowLoadLocalInfile=true", "db.example.com"],
        ["sqlserver", "Data Source=tcp:db.example.com,1433;User ID=app;Password=secret;Encrypt=true;Connect Timeout=5", "TrustServerCertificate=true", "db.example.com"],
        ["oracle", "Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCL))(SECURITY=(SSL_SERVER_DN_MATCH=YES)));User ID=app;Password=secret;Connection Timeout=5", "Wallet Location=/tmp/wallet", "db.example.com"],
        ["snowflake", "account=acme;user=app;password=secret;scheme=https;connection_timeout=5", "insecuremode=true", "acme.snowflakecomputing.com"],
        ["redshift", "Host=db.example.com;Database=analytics;Username=app;Password=secret;SSL Mode=Require;Timeout=5", "Options=-c statement_timeout=0", "db.example.com"]
    ];

    [UnitTheory]
    [MemberData(nameof(Cases))]
    public async Task ResolveConnectionString_ProviderPolicyChecksKeywordsAndResolvedHost(
        string provider, string allowedString, string refusedSetting, string expectedHost)
    {
        foreach (var secretReference in new[] { false, true })
        {
            var permitted = CreateResolver(provider, allowedString, secretReference, expectedHost);
            Assert.Equal(allowedString, await permitted.ResolveConnectionStringAsync("policy-test"));

            var forbiddenKeyword = CreateResolver(provider, allowedString + ";" + refusedSetting, secretReference, expectedHost);
            await Assert.ThrowsAsync<InvalidOperationException>(() => forbiddenKeyword.ResolveConnectionStringAsync("policy-test"));

            var forbiddenHost = CreateResolver(provider, allowedString, secretReference, "other.example.com");
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => forbiddenHost.ResolveConnectionStringAsync("policy-test"));
            Assert.Contains("host policy", failure.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [UnitTheory]
    [InlineData("Server=db.example.com,other.example.com;User ID=app;Password=secret;SSL Mode=Required")]
    public async Task ResolveConnectionString_MySqlChecksEveryHost(string connectionString)
    {
        var resolver = CreateResolver("mysql", connectionString, true, "db.example.com");
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveConnectionStringAsync("policy-test"));
    }

    private static SecureConnectionResolver CreateResolver(string provider, string connectionString, bool secretReference, string allowedHost)
    {
        var connection = secretReference
            ? DataConnection.CreateWithSecretReference("policy-test", DataConnection.SecretReferenceMetadataPlaceholder, 0,
                "analytics", "app", "env:POLICY_CONNECTION", "EnvironmentVariable", "test")
            : DataConnection.CreateWithEncryptedCredentials("policy-test", "db.example.com", 5432,
                "analytics", "app", [1], 1, "test");
        connection.Provider = provider;
        var registry = Substitute.For<ISecureConnectionRegistry>();
        registry.GetConnectionByNameAsync("policy-test", Arg.Any<CancellationToken>()).Returns(connection);
        var encryption = Substitute.For<IConnectionEncryptionService>();
        encryption.DecryptConnectionStringAsync(Arg.Any<byte[]>(), Arg.Any<int>()).Returns(connectionString);
        var secrets = Substitute.For<IRequestSecretReferenceResolver>();
        secrets.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(connectionString);
        var hosts = Substitute.For<IConnectionHostAllowlist>();
        hosts.IsEnforced.Returns(true);
        hosts.EvaluateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ConnectionHostDecision(string.Equals(call.ArgAt<string>(0), allowedHost, StringComparison.Ordinal), "test policy"));
        IConnectionDriver[] drivers =
        [
            new MySqlConnectionDriver(NullLogger<MySqlConnectionDriver>.Instance),
            new SqlServerConnectionDriver(NullLogger<SqlServerConnectionDriver>.Instance),
            new OracleConnectionDriver(NullLogger<OracleConnectionDriver>.Instance),
            new SnowflakeConnectionDriver(NullLogger<SnowflakeConnectionDriver>.Instance)
        ];
        return new(registry, encryption, secrets, NullLogger<SecureConnectionResolver>.Instance,
            new ConnectionDriverRegistry(drivers), hosts);
    }
}
