// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

public interface IDatabaseConnectionStringBuilder
{
    string BuildConnectionString(
        string host,
        int port,
        string databaseName,
        string username,
        string password,
        SslMode sslMode);

    Task<string> BuildConnectionStringAsync(DataConnection connection);

    bool ValidateConnectionString(string connectionString);
}
