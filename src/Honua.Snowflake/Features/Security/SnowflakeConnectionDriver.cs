// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
internal sealed partial class SnowflakeConnectionDriver : IConnectionDriver
{
    private readonly ILogger<SnowflakeConnectionDriver> _logger;

    public SnowflakeConnectionDriver(ILogger<SnowflakeConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.Snowflake;

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Snowflake addresses an account identifier rather than a host:port; the console surfaces the
        // "host" field as "Account" for this provider, and the "database" field as the default database.
        var account = EscapeValue(target.Host);
        var user = EscapeValue(target.Username);
        var password = EscapeValue(target.Password);
        var database = EscapeValue(target.Database);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"account={account};user={user};password={password};db={database}");
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
        catch (Exception ex)
        {
            LogProbeFailed(ex);
            return ConnectionHealthStatus.Unhealthy;
        }
    }

    // Snowflake.Data accepts a semicolon-delimited key=value connection string; values containing
    // ';' or '=' must be wrapped in braces. Mirror the driver's documented escaping.
    private static string EscapeValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Contains(';', StringComparison.Ordinal) || value.Contains('=', StringComparison.Ordinal)
            ? "{" + value + "}"
            : value;
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
