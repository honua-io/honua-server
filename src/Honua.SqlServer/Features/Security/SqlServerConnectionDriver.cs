// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;

namespace Honua.SqlServer.Features.Security;

/// <summary>
/// SQL Server <see cref="IConnectionDriver"/>: builds a Microsoft.Data.SqlClient connection string
/// (<c>Data Source=host,port</c>) and probes health with a real <see cref="SqlConnection"/> + <c>SELECT 1</c>.
/// </summary>
internal sealed partial class SqlServerConnectionDriver : IConnectionDriver
{
    private readonly ILogger<SqlServerConnectionDriver> _logger;

    public SqlServerConnectionDriver(ILogger<SqlServerConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.SqlServer;

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{target.Host},{target.Port.ToString(CultureInfo.InvariantCulture)}",
            InitialCatalog = target.Database,
            UserID = target.Username,
            Password = target.Password,
            ConnectTimeout = 5
        };

        // SQL Server encrypts by default. "Disable" turns it off; the verify modes require a chain-valid
        // server certificate, while the non-verify encrypted modes accept a self-signed/dev certificate.
        if (target.SslMode == CoreSslMode.Disable)
        {
            builder.Encrypt = false;
        }
        else
        {
            builder.Encrypt = true;
            builder.TrustServerCertificate = target.SslMode is not (CoreSslMode.VerifyCa or CoreSslMode.VerifyFull);
        }

        return builder.ConnectionString;
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
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

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning, Message = "SQL Server connection probe failed")]
    private partial void LogProbeFailed(Exception exception);
}

/// <summary>DI helper that registers the SQL Server connection driver.</summary>
public static class SqlServerConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the SQL Server <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    public static IServiceCollection AddSqlServerConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, SqlServerConnectionDriver>());
        return services;
    }
}
