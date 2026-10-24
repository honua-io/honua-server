// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Data.SqlClient;

namespace Honua.Db.SqlServer.Features.Security;

internal static class SqlServerConnectionSecurity
{
    public static string RequireEncryption(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // codeql[cs/insecure-sql-connection]: every incoming string, including one that
        // sets Encrypt=false, is re-parsed here and Encrypt is forced true before any
        // SqlConnection is opened. CodeQL traces the pre-rewrite test value into this
        // builder and does not model the object-initializer assignment.
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Encrypt = true
        };

        return builder.ConnectionString;
    }
}
