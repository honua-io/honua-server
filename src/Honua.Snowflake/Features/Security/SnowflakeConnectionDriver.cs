// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Snowflake.Data.Client;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;

namespace Honua.Snowflake.Features.Security;

/// <summary>
/// Snowflake <see cref="IConnectionDriver"/>: builds a <c>Snowflake.Data</c> connection string
/// (<c>account=...;user=...;password=...;db=...</c>, where the connection's "host" is the Snowflake
/// account identifier and the "database" is the default Snowflake database) and probes health with a
/// real <see cref="SnowflakeDbConnection"/> + <c>SELECT 1</c>.
/// </summary>
internal sealed partial class SnowflakeConnectionDriver : IConnectionDriver, ISecureConnectionStringPolicy
{
    private readonly ILogger<SnowflakeConnectionDriver> _logger;

    public SnowflakeConnectionDriver(ILogger<SnowflakeConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.Snowflake;

    public ConnectionStringSecurity InspectConnectionString(string connectionString)
    {
        // Snowflake.Data itself uses DbConnectionStringBuilder rather than exposing a
        // typed builder. Keep its explicit endpoint/credential form equally bounded.
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        ConnectionStringKeywordPolicy.EnsureAllowed(builder,
            "account", "host", "port", "scheme", "user", "password", "db", "schema", "warehouse", "role",
            "authenticator", "token", "connection_timeout", "pooling", "poolingEnabled", "minPoolSize", "maxPoolSize");
        if (builder.TryGetValue("scheme", out var scheme) &&
            !string.Equals(scheme.ToString(), "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snowflake connections must use HTTPS.");
        }
        if (builder.TryGetValue("authenticator", out var authenticator) &&
            !string.Equals(authenticator.ToString(), "snowflake", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(authenticator.ToString(), "oauth", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snowflake authenticator must not invoke an external endpoint or local credential file.");
        }
        string host;
        if (builder.TryGetValue("host", out var configuredHost))
        {
            host = configuredHost.ToString() ?? string.Empty;
        }
        else if (builder.TryGetValue("account", out var account) &&
                 !string.IsNullOrWhiteSpace(account.ToString()) &&
                 account.ToString()!.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            // Snowflake.Data 5.7 defaults allowUnderscoresInHost=false and folds
            // account underscores to hyphens before composing its destination.
            // https://github.com/snowflakedb/snowflake-connector-net/blob/v5.7.0/Snowflake.Data/Core/Session/SFSessionProperty.cs
            host = account.ToString()!.Replace('_', '-') + ".snowflakecomputing.com";
        }
        else
        {
            throw new ArgumentException("Snowflake connection must name an explicit account or host.");
        }
        return new(ConnectionStringKeywordPolicy.SplitHosts(host), true);
    }

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Snowflake addresses an account identifier rather than a host:port; the console surfaces the
        // "host" field as "Account" for this provider, and the "database" field as the default database.
        // Snowflake.Data parses the string with DbConnectionStringBuilder, so build it with the same type:
        // values containing ';', '=' or quotes are quoted and cannot add keys of their own (SEC-23).
        var builder = new DbConnectionStringBuilder
        {
            ["account"] = target.Host ?? string.Empty,
            ["user"] = target.Username ?? string.Empty,
            ["password"] = target.Password ?? string.Empty,
            ["db"] = target.Database ?? string.Empty
        };

        return builder.ConnectionString;
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SnowflakeDbConnection { ConnectionString = connectionString };
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return ConnectionProbe.Evaluate(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentionally generic: this is a connection-test probe (admin "test this
        // connection" flow) that must report Unhealthy rather than throw for any
        // driver/network/auth failure; the exception is logged via LogProbeFailed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogProbeFailed(ex);
            return ConnectionHealthStatus.Unhealthy;
        }
    }

    [LoggerMessage(EventId = 7204, Level = LogLevel.Warning, Message = "Snowflake connection probe failed")]
    private partial void LogProbeFailed(Exception exception);
}

/// <summary>DI helper that registers the Snowflake connection driver.</summary>
public static class SnowflakeConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the Snowflake <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    public static IServiceCollection AddSnowflakeConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, SnowflakeConnectionDriver>());
        return services;
    }
}
