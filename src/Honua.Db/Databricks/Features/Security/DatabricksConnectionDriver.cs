// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ConnectionHealthStatus = Honua.Core.Features.Security.Domain.ConnectionHealthStatus;

namespace Honua.Db.Databricks.Features.Security;

/// <summary>
/// Databricks <see cref="IConnectionDriver"/>. The admin connection fields map to a SQL
/// Warehouse as host (workspace), database (warehouse id), and password (access token).
/// The probe submits <c>SELECT 1</c> through <see cref="DatabricksStatementClient"/>.
/// </summary>
internal sealed partial class DatabricksConnectionDriver : IConnectionDriver, ISecureConnectionStringPolicy
{
    private readonly ILogger<DatabricksConnectionDriver> _logger;
    private readonly HttpMessageHandler? _handler;

    /// <summary>Creates a driver that probes with a default HTTP handler.</summary>
    /// <param name="logger">Logger for probe failures.</param>
    public DatabricksConnectionDriver(ILogger<DatabricksConnectionDriver> logger)
        : this(logger, handler: null)
    {
    }

    /// <summary>Creates a driver that probes through <paramref name="handler"/> when set.</summary>
    /// <param name="logger">Logger for probe failures.</param>
    /// <param name="handler">Optional HTTP handler. The caller retains ownership.</param>
    internal DatabricksConnectionDriver(ILogger<DatabricksConnectionDriver> logger, HttpMessageHandler? handler)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _handler = handler;
    }

    /// <inheritdoc />
    public string Provider => DataProviderNames.Databricks;

    /// <inheritdoc />
    public ConnectionStringSecurity InspectConnectionString(string connectionString)
    {
        var (endpoint, _, _) = Parse(connectionString);
        return new ConnectionStringSecurity(ConnectionStringKeywordPolicy.SplitHosts(endpoint.IdnHost), true);
    }

    /// <inheritdoc />
    public string BuildConnectionString(ConnectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var endpoint = RequireHttpsEndpoint(target.Host, target.Port);
        var builder = new DbConnectionStringBuilder
        {
            ["Host"] = endpoint.IdnHost,
            ["WarehouseId"] = target.Database ?? string.Empty,
            ["Token"] = target.Password ?? string.Empty
        };
        if (!endpoint.IsDefaultPort)
        {
            builder["Port"] = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        }

        return builder.ConnectionString;
    }

    /// <inheritdoc />
    public async Task<ConnectionHealthStatus> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (endpoint, warehouseId, token) = Parse(connectionString);
            using var client = _handler is null
                ? new HttpClient()
                : new HttpClient(_handler, disposeHandler: false);
            client.BaseAddress = endpoint;
            client.Timeout = TimeSpan.FromSeconds(20);

            var statementClient = new DatabricksStatementClient(client, Options.Create(new DatabricksOptions
            {
                Host = endpoint.GetLeftPart(UriPartial.Authority),
                WarehouseId = warehouseId,
                Token = token,
                CommandTimeoutSeconds = 10,
                PollIntervalMilliseconds = 20
            }));
            var result = await statementClient
                .ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), cancellationToken)
                .ConfigureAwait(false);

            object? scalar = null;
            if (result.Rows.Count > 0 && result.Rows[0].Length > 0)
            {
                scalar = Unwrap(result.Rows[0][0]);
            }

            return ConnectionProbe.Evaluate(scalar);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout (and the statement client's own deadline) surface as
            // cancellation even when the caller did not cancel. That is a failed probe.
            LogProbeTimedOut(ex);
            return ConnectionHealthStatus.Unhealthy;
        }
        catch (TimeoutException ex)
        {
            LogProbeTimedOut(ex);
            return ConnectionHealthStatus.Unhealthy;
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

    [LoggerMessage(EventId = 7205, Level = LogLevel.Warning, Message = "Databricks connection probe failed")]
    private partial void LogProbeFailed(Exception exception);

    [LoggerMessage(EventId = 7206, Level = LogLevel.Warning, Message = "Databricks connection probe timed out")]
    private partial void LogProbeTimedOut(Exception exception);

    private static (Uri Endpoint, string WarehouseId, string Token) Parse(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        ConnectionStringKeywordPolicy.EnsureAllowed(builder, "Host", "Port", "WarehouseId", "Token");
        if (!builder.TryGetValue("Host", out var hostValue) || string.IsNullOrWhiteSpace(hostValue?.ToString()))
        {
            throw new ArgumentException("Databricks connection must name a workspace host.");
        }

        if (!builder.TryGetValue("WarehouseId", out var warehouseValue) || string.IsNullOrWhiteSpace(warehouseValue?.ToString()))
        {
            throw new ArgumentException("Databricks connection must name a SQL warehouse id.");
        }

        if (!builder.TryGetValue("Token", out var tokenValue) || string.IsNullOrWhiteSpace(tokenValue?.ToString()))
        {
            throw new ArgumentException("Databricks connection must name an access token.");
        }

        var port = 0;
        if (builder.TryGetValue("Port", out var portValue)
            && portValue is not null
            && !int.TryParse(portValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            throw new ArgumentException("Databricks connection port must be an integer.");
        }

        return (RequireHttpsEndpoint(hostValue.ToString()!, port), warehouseValue.ToString()!, tokenValue.ToString()!);
    }

    private static Uri RequireHttpsEndpoint(string? host, int port)
    {
        var trimmed = host?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Databricks connections require an HTTPS workspace host.");
        }

        Uri uri;
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri!) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Databricks connections require an HTTPS workspace host.");
            }
        }
        else
        {
            var withScheme = port > 0
                ? string.Create(CultureInfo.InvariantCulture, $"https://{trimmed}:{port}")
                : "https://" + trimmed;
            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out uri!))
            {
                throw new ArgumentException("Databricks connections require an HTTPS workspace host.");
            }
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Length > 1)
        {
            throw new ArgumentException("Databricks workspace host must not include a path, query, or user info.");
        }

        var builder = new UriBuilder(Uri.UriSchemeHttps, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port);
        return builder.Uri;
    }

    private static object? Unwrap(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var asLong) => asLong,
        JsonValueKind.Number when value.TryGetDouble(out var asDouble) => asDouble,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.GetRawText()
    };
}

/// <summary>DI helper that registers the Databricks connection driver.</summary>
public static class DatabricksConnectionDriverServiceCollectionExtensions
{
    /// <summary>Registers the Databricks <see cref="IConnectionDriver"/> for provider-aware connection testing.</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddDatabricksConnectionDriver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionDriver, DatabricksConnectionDriver>());
        return services;
    }
}
