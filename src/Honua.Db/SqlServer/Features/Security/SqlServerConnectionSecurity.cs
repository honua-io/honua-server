// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Data.SqlClient;

namespace Honua.Db.SqlServer.Features.Security;

internal static class SqlServerConnectionSecurity
{
    public static string RequireEncryption(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Encrypt = true
        };

        return builder.ConnectionString;
    }
}
