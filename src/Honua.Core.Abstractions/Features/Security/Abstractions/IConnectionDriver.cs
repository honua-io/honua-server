// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

public static class ConnectionProbe
{
    public static ConnectionHealthStatus Evaluate(object? scalarResult)
    {
        if (scalarResult is null or DBNull)
        {
            return ConnectionHealthStatus.Warning;
        }

        try
        {
            return Convert.ToInt64(scalarResult, CultureInfo.InvariantCulture) == 1
                ? ConnectionHealthStatus.Healthy
                : ConnectionHealthStatus.Warning;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return ConnectionHealthStatus.Warning;
        }
    }
}

public interface IConnectionDriver
{
    string Provider { get; }

    string BuildConnectionString(ConnectionTarget target);

    Task<ConnectionHealthStatus> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default);
}

public sealed record ConnectionTarget(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    SslMode SslMode);

public interface IConnectionDriverRegistry
{
    bool Supports(string? provider);

    IConnectionDriver? Find(string? provider);
}
