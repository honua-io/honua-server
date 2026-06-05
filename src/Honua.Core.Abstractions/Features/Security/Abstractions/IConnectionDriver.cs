// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Shared evaluation of an engine connection driver's liveness-probe scalar result (<c>SELECT 1</c> /
/// <c>SELECT 1 FROM DUAL</c>): <see cref="ConnectionHealthStatus.Healthy"/> when it is 1,
/// <see cref="ConnectionHealthStatus.Warning"/> for a null/non-1/unparseable result.
/// </summary>
public static class ConnectionProbe
{
    /// <summary>Maps a liveness-probe scalar result to a <see cref="ConnectionHealthStatus"/>.</summary>
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

/// <summary>
/// Per-provider connection driver for admin secure connections. Builds the engine-specific connection
/// string from connection parameters and probes health by opening a real connection with that engine's
/// ADO.NET provider. One implementation per supported engine (postgis/postgresql, mysql, sqlserver,
/// oracle); the <see cref="IConnectionDriverRegistry"/> selects the right one by normalized provider name.
/// This is what makes the admin connection test provider-aware instead of always speaking PostgreSQL.
/// </summary>
public interface IConnectionDriver
{
    /// <summary>The normalized provider this driver handles (a <c>DataProviderNames</c> value).</summary>
    string Provider { get; }

    /// <summary>Builds the engine-specific connection string from the supplied connection target.</summary>
    string BuildConnectionString(ConnectionTarget target);

    /// <summary>
    /// Opens a real connection with the engine's ADO.NET provider and runs a trivial liveness query,
    /// returning <see cref="ConnectionHealthStatus.Healthy"/> on success, <see cref="ConnectionHealthStatus.Unhealthy"/>
    /// when the connection cannot be opened, or <see cref="ConnectionHealthStatus.Warning"/> on an unexpected probe result.
    /// </summary>
    Task<ConnectionHealthStatus> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default);
}

/// <summary>
/// Engine-agnostic connection parameters used to build a provider connection string. Mirrors the fields the
/// admin connection-create / connection-test endpoints collect.
/// </summary>
public sealed record ConnectionTarget(
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    SslMode SslMode);

/// <summary>
/// Resolves the <see cref="IConnectionDriver"/> for a (possibly un-normalized) provider name. Registered with
/// every engine driver the server composes, so the admin connection endpoints can build and test connections
/// for any supported engine.
/// </summary>
public interface IConnectionDriverRegistry
{
    /// <summary>True when a driver is registered for <paramref name="provider"/> (after normalization).</summary>
    bool Supports(string? provider);

    /// <summary>Returns the driver for <paramref name="provider"/>, or <see langword="null"/> when unsupported.</summary>
    IConnectionDriver? Find(string? provider);
}
