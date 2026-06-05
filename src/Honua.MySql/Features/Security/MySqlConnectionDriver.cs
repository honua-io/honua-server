// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;

namespace Honua.MySql.Features.Security;

/// <summary>
/// MySQL/MariaDB <see cref="IConnectionDriver"/>: builds a MySqlConnector connection string and probes health
/// with a real <see cref="MySqlConnection"/> + <c>SELECT 1</c>.
/// </summary>
internal sealed partial class MySqlConnectionDriver : IConnectionDriver
{
    private readonly ILogger<MySqlConnectionDriver> _logger;

    public MySqlConnectionDriver(ILogger<MySqlConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.MySql;

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new MySqlConnectionStringBuilder
        {
            Server = target.Host,
            Port = (uint)target.Port,
            Database = target.Database,
            UserID = target.Username,
            Password = target.Password,
            SslMode = MapSslMode(target.SslMode),
            ConnectionTimeout = 5
        }.ConnectionString;
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
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

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning, Message = "MySQL connection probe failed")]
    private partial void LogProbeFailed(Exception exception);

    private static MySqlSslMode MapSslMode(CoreSslMode sslMode) => sslMode switch
    {
        CoreSslMode.Disable => MySqlSslMode.None,
        CoreSslMode.Allow => MySqlSslMode.Preferred,
        CoreSslMode.Prefer => MySqlSslMode.Preferred,
        CoreSslMode.Require => MySqlSslMode.Required,
        CoreSslMode.VerifyCa => MySqlSslMode.VerifyCA,
        CoreSslMode.VerifyFull => MySqlSslMode.VerifyFull,
        _ => MySqlSslMode.Preferred
    };
}

/// <summary>DI helper that registers the MySQL connection driver.</summary>
public static class MySqlConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the MySQL/MariaDB <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    public static IServiceCollection AddMySqlConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, MySqlConnectionDriver>());
        return services;
    }
}
