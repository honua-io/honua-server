// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;

namespace Honua.Postgres.Features.Security;

/// <summary>
/// PostgreSQL/PostGIS <see cref="IConnectionDriver"/>: builds an Npgsql connection string and probes health
/// with a real <see cref="NpgsqlConnection"/> + <c>SELECT 1</c>.
/// </summary>
internal sealed partial class PostgresConnectionDriver : IConnectionDriver
{
    private readonly ILogger<PostgresConnectionDriver> _logger;

    public PostgresConnectionDriver(ILogger<PostgresConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.Postgis;

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new NpgsqlConnectionStringBuilder
        {
            Host = target.Host,
            Port = target.Port,
            Database = target.Database,
            Username = target.Username,
            Password = target.Password,
            SslMode = MapSslMode(target.SslMode)
        }.ConnectionString;
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
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

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "PostgreSQL connection probe failed")]
    private partial void LogProbeFailed(Exception exception);

    private static Npgsql.SslMode MapSslMode(CoreSslMode sslMode) => sslMode switch
    {
        CoreSslMode.Disable => Npgsql.SslMode.Disable,
        CoreSslMode.Allow => Npgsql.SslMode.Allow,
        CoreSslMode.Prefer => Npgsql.SslMode.Prefer,
        CoreSslMode.Require => Npgsql.SslMode.Require,
        CoreSslMode.VerifyCa => Npgsql.SslMode.VerifyCA,
        CoreSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
        _ => Npgsql.SslMode.Prefer
    };
}

/// <summary>DI helper that registers the PostgreSQL connection driver.</summary>
public static class PostgresConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the PostgreSQL/PostGIS <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    public static IServiceCollection AddPostgresConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, PostgresConnectionDriver>());
        return services;
    }
}
