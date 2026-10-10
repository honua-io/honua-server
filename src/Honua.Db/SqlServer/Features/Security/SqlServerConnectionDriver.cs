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

namespace Honua.Db.SqlServer.Features.Security;

/// <summary>
/// SQL Server <see cref="IConnectionDriver"/>: builds a Microsoft.Data.SqlClient connection string
/// (<c>Data Source=host,port</c>) and probes health with a real <see cref="SqlConnection"/> + <c>SELECT 1</c>.
/// </summary>
internal sealed partial class SqlServerConnectionDriver : IConnectionDriver, ISecureConnectionStringPolicy
{
    private readonly ILogger<SqlServerConnectionDriver> _logger;

    public SqlServerConnectionDriver(ILogger<SqlServerConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.SqlServer;

    public ConnectionStringSecurity InspectConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        ConnectionStringKeywordPolicy.EnsureAllowed(builder,
            "Data Source", "Initial Catalog", "User ID", "Password", "Encrypt", "Trust Server Certificate",
            "Connect Timeout", "Command Timeout", "Pooling", "Min Pool Size", "Max Pool Size",
            "Load Balance Timeout", "Connect Retry Count", "Connect Retry Interval", "Application Name",
            "Application Intent", "Multi Subnet Failover", "Multiple Active Result Sets", "Packet Size", "Enlist");
        if (builder.TrustServerCertificate)
        {
            throw new ArgumentException("Disabling server certificate validation is not permitted.");
        }
        var source = builder.DataSource.Trim();
        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            source = source[4..].Trim();
        }
        else if (source.Contains(':') && !source.StartsWith('['))
        {
            throw new ArgumentException("SQL Server connections must use a network host.");
        }
        if (source.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("LocalDB is not a permitted network destination.");
        }
        var host = source.Split(',')[0].Split('\\')[0].Trim();
        if (Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("SQL Server connections must name a network host.");
        }
        return new(ConnectionStringKeywordPolicy.SplitHosts(host),
            builder.Encrypt != SqlConnectionEncryptOption.Optional);
    }

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

        // SQL Server encrypts by default. "Disable" turns it off; encrypted modes require
        // a chain-valid certificate, including the Require mode.
        if (target.SslMode == CoreSslMode.Disable)
        {
            builder.Encrypt = false;
        }
        else
        {
            builder.Encrypt = true;
            builder.TrustServerCertificate = false;
        }

        return builder.ConnectionString;
    }

    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // `connectionString` is not guaranteed to have flowed through BuildConnectionString above:
            // the admin secure-connection test/probe paths can source it from an operator-supplied secret
            // reference instead (SecureConnectionEndpoints.HandleTestDraftConnection,
            // SecureConnectionResolver.ResolveConnectionStringInternalAsync), which bypasses this driver's
            // own Encrypt/TrustServerCertificate mapping entirely. Re-parse and force TLS so a secret-store
            // value that omits or disables encryption can never open a plaintext TDS session; per the MVP
            // deferrals, secure connections are "encrypted or secret references only" (never unencrypted).
            var encryptedConnectionString = SqlServerConnectionSecurity.RequireEncryption(connectionString);
            // codeql[cs/insecure-sql-connection]: RequireEncryption re-parses every incoming
            // connection string and overwrites Encrypt with true before SqlConnection sees it.
            // CodeQL traces the original test-endpoint value here but does not model that rewrite.
            await using var connection = new SqlConnection(encryptedConnectionString);
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
