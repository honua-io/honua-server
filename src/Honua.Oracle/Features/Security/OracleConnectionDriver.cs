// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;

namespace Honua.Oracle.Features.Security;

/// <summary>
/// Oracle <see cref="IConnectionDriver"/>: builds an Oracle.ManagedDataAccess Easy Connect string
/// (<c>Data Source=host:port/service</c>, where the connection's "database" is the service name / SID) and
/// probes health with a real <see cref="OracleConnection"/> + <c>SELECT 1 FROM DUAL</c>.
/// </summary>
internal sealed partial class OracleConnectionDriver : IConnectionDriver
{
    private readonly ILogger<OracleConnectionDriver> _logger;

    public OracleConnectionDriver(ILogger<OracleConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.Oracle;

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Oracle addresses a service name / SID rather than a database; the console surfaces the "database"
        // field as "Service name" for this provider. Easy Connect syntax: host:port/service.
        return new OracleConnectionStringBuilder
        {
            UserID = target.Username,
            Password = target.Password,
            DataSource = $"{target.Host}:{target.Port.ToString(CultureInfo.InvariantCulture)}/{target.Database}",
            ConnectionTimeout = 5
        }.ConnectionString;
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM DUAL";
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

    [LoggerMessage(EventId = 7104, Level = LogLevel.Warning, Message = "Oracle connection probe failed")]
    private partial void LogProbeFailed(Exception exception);
}

/// <summary>DI helper that registers the Oracle connection driver.</summary>
public static class OracleConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the Oracle <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    public static IServiceCollection AddOracleConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, OracleConnectionDriver>());
        return services;
    }
}
