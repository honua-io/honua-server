// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Oracle.Features.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;

namespace Honua.Db.Oracle.Tests;

public sealed class OracleConnectionDriverTests
{
    [Theory]
    [InlineData(SslMode.Require)]
    [InlineData(SslMode.VerifyCa)]
    public void BuildConnectionString_SRV_AUTH_008_SecureModeUsesTcps(SslMode sslMode)
    {
        var connectionString = Build(sslMode);
        var builder = new OracleConnectionStringBuilder(connectionString);

        Assert.Contains("PROTOCOL=TCPS", builder.DataSource, StringComparison.Ordinal);
        Assert.Contains("SSL_SERVER_DN_MATCH=YES", builder.DataSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConnectionString_SRV_AUTH_008_VerifyFullEnablesServerIdentityMatch()
    {
        var connectionString = Build(SslMode.VerifyFull);
        var builder = new OracleConnectionStringBuilder(connectionString);

        Assert.Contains("PROTOCOL=TCPS", builder.DataSource, StringComparison.Ordinal);
        Assert.Contains("SSL_SERVER_DN_MATCH=YES", builder.DataSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConnectionString_SRV_AUTH_008_DisabledModeDoesNotClaimTcps()
    {
        var connectionString = Build(SslMode.Disable);
        Assert.DoesNotContain("TCPS", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    private static string Build(SslMode sslMode)
    {
        var driver = new OracleConnectionDriver(NullLogger<OracleConnectionDriver>.Instance);
        return driver.BuildConnectionString(new ConnectionTarget(
            "db.example", 1521, "ORCLPDB1", "app", "secret", sslMode));
    }
}
