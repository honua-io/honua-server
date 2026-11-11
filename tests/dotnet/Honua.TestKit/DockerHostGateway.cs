// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.TestKit;

internal static class DockerHostGateway
{
    public const string DockerHostAlias = "host.docker.internal";

    // The test database is published on the Docker host, which a container reaches through the host gateway.
    public static string ConnectionStringReachableFromContainer(string hostConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(hostConnectionString);
        if (builder.Host is "localhost" or "127.0.0.1" or "::1")
        {
            builder.Host = DockerHostAlias;
        }

        return builder.ConnectionString;
    }
}
