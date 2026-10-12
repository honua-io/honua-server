// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.SqlServer.Features.Security;
using Microsoft.Data.SqlClient;

namespace Honua.Db.SqlServer.Tests;

public sealed class SqlServerConnectionSecurityTests
{
    [Theory]
    [InlineData("Server=sql.example;Database=honua;User Id=user;Password=secret")]
    [InlineData("Server=sql.example;Database=honua;Integrated Security=true;Encrypt=false")]
    public void RequireEncryption_ForcesEncryptTrue(string connectionString)
    {
        var secured = SqlServerConnectionSecurity.RequireEncryption(connectionString);

        var builder = new SqlConnectionStringBuilder(secured);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
    }

    [Fact]
    public void RequireEncryption_PreservesExplicitCertificateValidationPolicy()
    {
        const string connectionString =
            "Server=sql.example;Database=honua;Integrated Security=true;Encrypt=false;TrustServerCertificate=false";

        var secured = SqlServerConnectionSecurity.RequireEncryption(connectionString);

        var builder = new SqlConnectionStringBuilder(secured);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
    }
}
