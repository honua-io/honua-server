// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Db.Oracle.Features.Security;

/// <summary>
/// Oracle <see cref="IConnectionDriver"/>: builds an Oracle.ManagedDataAccess Easy Connect string
/// (<c>Data Source=host:port/service</c>, where the connection's "database" is the service name / SID) and
/// probes health with a real <see cref="OracleConnection"/> + <c>SELECT 1 FROM DUAL</c>.
/// </summary>
internal sealed partial class OracleConnectionDriver : IConnectionDriver, ISecureConnectionStringPolicy
{
    private readonly ILogger<OracleConnectionDriver> _logger;

    public OracleConnectionDriver(ILogger<OracleConnectionDriver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => DataProviderNames.Oracle;

    public ConnectionStringSecurity InspectConnectionString(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        ConnectionStringKeywordPolicy.EnsureAllowed(builder,
            "User Id", "Password", "Data Source", "Connection Timeout", "Pooling",
            "Min Pool Size", "Max Pool Size", "Incr Pool Size", "Decr Pool Size", "Connection Lifetime",
            "Validate Connection", "Statement Cache Size", "Statement Cache Purge", "Enlist");
        var source = builder.DataSource.Trim();
        if (source.StartsWith('('))
        {
            // Deliberately accept the explicit single-address descriptor we generate,
            // not TNS aliases or arbitrary descriptors with wallets/startup options.
            var match = SafeDescriptorPattern().Match(source);
            if (!match.Success ||
                (match.Groups["protocol"].Value.Equals("TCPS", StringComparison.OrdinalIgnoreCase) && !match.Groups["security"].Success))
            {
                throw new ArgumentException("Oracle data source must be an explicit safe network address.");
            }
            return new(ConnectionStringKeywordPolicy.SplitHosts(match.Groups["host"].Value),
                match.Groups["protocol"].Value.Equals("TCPS", StringComparison.OrdinalIgnoreCase));
        }

        var tls = source.StartsWith("tcps://", StringComparison.OrdinalIgnoreCase);
        if (tls)
        {
            throw new ArgumentException("Oracle TLS connections require an explicit descriptor with server identity validation.");
        }
        if (source.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            source = source[(source.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }
        if (!Uri.TryCreate("oracle://" + source.TrimStart('/'), UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(endpoint.Host) || string.IsNullOrWhiteSpace(endpoint.AbsolutePath.Trim('/')) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Oracle data source must be an explicit host and service name.");
        }
        return new([endpoint.IdnHost.Trim('[', ']')], tls);
    }

    [GeneratedRegex(@"^\s*\(DESCRIPTION\s*=\s*\(ADDRESS\s*=\s*\(PROTOCOL\s*=\s*(?<protocol>TCPS|TCP)\s*\)\s*\(HOST\s*=\s*(?<host>[^\s();=]+)\s*\)\s*\(PORT\s*=\s*[0-9]+\s*\)\s*\)\s*\(CONNECT_DATA\s*=\s*\(SERVICE_NAME\s*=\s*[^\s();=]+\s*\)\s*\)\s*(?<security>\(SECURITY\s*=\s*\(SSL_SERVER_DN_MATCH\s*=\s*YES\s*\)\s*\))?\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SafeDescriptorPattern();

    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Oracle addresses a service name / SID rather than a database; the console surfaces the "database"
        // field as "Service name" for this provider. Easy Connect syntax: host:port/service.
        var useTls = target.SslMode is CoreSslMode.Require or CoreSslMode.VerifyCa or CoreSslMode.VerifyFull;
        var dataSource = useTls
            ? BuildTcpsDescriptor(target, verifyServerIdentity: true)
            : $"{target.Host}:{target.Port.ToString(CultureInfo.InvariantCulture)}/{target.Database}";

        return new OracleConnectionStringBuilder
        {
            UserID = target.Username,
            Password = target.Password,
            DataSource = dataSource,
            ConnectionTimeout = 5
        }.ConnectionString;
    }

    private static string BuildTcpsDescriptor(ConnectionTarget target, bool verifyServerIdentity)
    {
        var dnMatch = verifyServerIdentity ? "YES" : "NO";
        return string.Create(CultureInfo.InvariantCulture,
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST={target.Host})(PORT={target.Port}))" +
            $"(CONNECT_DATA=(SERVICE_NAME={target.Database}))(SECURITY=(SSL_SERVER_DN_MATCH={dnMatch})))");
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
        // Intentionally generic: this is a connection-test probe (admin "test this
        // connection" flow) that must report Unhealthy rather than throw for any
        // driver/network/auth failure; the exception is logged via LogProbeFailed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
