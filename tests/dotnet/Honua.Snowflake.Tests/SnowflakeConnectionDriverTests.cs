// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Snowflake.Features.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Snowflake.Tests;

/// <summary>
/// SEC-23: the Snowflake connection string carries exactly the account, user, password and
/// database keys, with every value quoted so it parses back to itself.
/// </summary>
public class SnowflakeConnectionDriverTests
{
    [Theory]
    [InlineData("plain-password")]
    [InlineData("pa;ss=word")]
    [InlineData("x;host=other.example;insecuremode=true")]
    [InlineData("{braced};value")]
    [InlineData("quote\"and'apostrophe")]
    public void BuildConnectionString_PasswordWithSeparators_ParsesBackToTheSameFourKeys(string password)
    {
        var driver = new SnowflakeConnectionDriver(NullLogger<SnowflakeConnectionDriver>.Instance);

        var connectionString = driver.BuildConnectionString(
            new ConnectionTarget("acme-xy12345", 443, "ANALYTICS", "loader", password, SslMode.Require));

        var parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
        Assert.Equal(
            ["account", "db", "password", "user"],
            parsed.Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("acme-xy12345", parsed["account"]);
        Assert.Equal("loader", parsed["user"]);
        Assert.Equal(password, parsed["password"]);
        Assert.Equal("ANALYTICS", parsed["db"]);
    }
}
