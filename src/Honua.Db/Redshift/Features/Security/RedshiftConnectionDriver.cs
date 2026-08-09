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

namespace Honua.Redshift.Features.Security;

/// <summary>
/// Amazon Redshift <see cref="IConnectionDriver"/>: builds an Npgsql connection string and probes
/// health with a real <see cref="NpgsqlConnection"/> + <c>SELECT 1</c>. Redshift speaks the
/// PostgreSQL wire protocol, so the Npgsql driver connects without modification.
/// </summary>
internal sealed partial class RedshiftConnectionDriver : IConnectionDriver
{
    private readonly ILogger<RedshiftConnectionDriver> _logger;

    public RedshiftConnectionDriver(ILogger<RedshiftConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.Redshift;

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
            SslMode = MapSslMode(target.SslMode),
            // Redshift does not implement the PostgreSQL extended/binary protocol negotiation the
            // same way; Npgsql's server-version probe is unnecessary here and disabling redshift's
            // unsupported messages is handled by the server itself. ConnectTimeout keeps probes fast.
            Timeout = 5
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
        // Intentionally generic: this is a connection-test probe (admin "test this
        // connection" flow) that must report Unhealthy rather than throw for any
        // driver/network/auth failure; the exception is logged via LogProbeFailed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogProbeFailed(ex);
            return ConnectionHealthStatus.Unhealthy;
        }
    }

    [LoggerMessage(EventId = 7203, Level = LogLevel.Warning, Message = "Redshift connection probe failed")]
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

/// <summary>DI helper that registers the Redshift connection driver.</summary>
public static class RedshiftConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the Redshift <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    public static IServiceCollection AddRedshiftConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, RedshiftConnectionDriver>());
        return services;
    }
}
