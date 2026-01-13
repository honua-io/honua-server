// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Security.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security;

/// <summary>
/// Database connection provider that can optionally use secure connection registry.
/// </summary>
/// <remarks>
/// This provider maintains backward compatibility with the existing DefaultConnection
/// configuration while adding the capability to resolve connections from the secure
/// connection registry when a connection name is specified.
///
/// Usage patterns:
/// 1. Legacy mode: Uses DefaultConnection from configuration (existing behavior)
/// 2. Secure mode: Uses named connection from secure registry
/// 3. Mixed mode: Falls back to DefaultConnection if named connection not found
/// </remarks>
internal sealed class SecureConnectionAwareDatabaseProvider : IDatabaseConnectionProvider
{
    private readonly IDatabaseConnectionProvider _defaultProvider;
    private readonly ISecureConnectionResolver _secureResolver;
    private readonly SecureConnectionDataSourceCache _dataSourceCache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SecureConnectionAwareDatabaseProvider> _logger;
    private readonly ISchemaContext? _schemaContext;
    private readonly IActiveDbConnectionTracker? _activeDbConnectionTracker;
    private readonly string? _namedConnectionToUse;

    // Logger message delegates for performance
    private static readonly Action<ILogger, string, Exception?> _logSecureConnectionConfigured =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, nameof(_logSecureConnectionConfigured)),
            "Secure connection-aware provider configured to use named connection: {ConnectionName}");

    private static readonly Action<ILogger, Exception?> _logLegacyMode =
        LoggerMessage.Define(LogLevel.Debug, new EventId(2, nameof(_logLegacyMode)),
            "Secure connection-aware provider using legacy DefaultConnection mode");

    private static readonly Action<ILogger, string, Exception?> _logSecureConnectionOpened =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(3, nameof(_logSecureConnectionOpened)),
            "Opened secure connection using named connection: {ConnectionName}");

    private static readonly Action<ILogger, string, Exception?> _logSecureConnectionFallback =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, nameof(_logSecureConnectionFallback)),
            "Failed to open secure connection '{ConnectionName}', falling back to default connection");

    private static readonly Action<ILogger, string, Exception?> _logConnectionHealthTestFailure =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5, nameof(_logConnectionHealthTestFailure)),
            "Failed to test secure connection health for '{ConnectionName}'");

    private static readonly Action<ILogger, Exception?> _logRetrieveConnectionsFailure =
        LoggerMessage.Define(LogLevel.Warning, new EventId(6, nameof(_logRetrieveConnectionsFailure)),
            "Failed to retrieve available secure connections");

    public SecureConnectionAwareDatabaseProvider(
        IDatabaseConnectionProvider defaultProvider,
        ISecureConnectionResolver secureResolver,
        SecureConnectionDataSourceCache dataSourceCache,
        IConfiguration configuration,
        ILogger<SecureConnectionAwareDatabaseProvider> logger,
        ISchemaContext? schemaContext = null,
        IActiveDbConnectionTracker? activeDbConnectionTracker = null)
    {
        _defaultProvider = defaultProvider ?? throw new ArgumentNullException(nameof(defaultProvider));
        _secureResolver = secureResolver ?? throw new ArgumentNullException(nameof(secureResolver));
        _dataSourceCache = dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaContext = schemaContext;
        _activeDbConnectionTracker = activeDbConnectionTracker;

        // Check if a specific named connection should be used
        _namedConnectionToUse = _configuration["Database:SecureConnection:Name"];

        if (!string.IsNullOrWhiteSpace(_namedConnectionToUse))
        {
            _logSecureConnectionConfigured(_logger, _namedConnectionToUse, null);
        }
        else
        {
            _logLegacyMode(_logger, null);
        }
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_namedConnectionToUse))
        {
            // Legacy mode - use default provider
            return await _defaultProvider.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            // Secure mode - resolve connection from registry
            var connectionString = await _secureResolver.ResolveConnectionStringAsync(
                _namedConnectionToUse, cancellationToken);

            var dataSource = _dataSourceCache.GetOrCreate(connectionString);
            var connection = await dataSource.OpenConnectionWithRetryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await SchemaSearchPath.ApplyAsync(connection, _schemaContext?.CurrentSchema, cancellationToken).ConfigureAwait(false);

            _logSecureConnectionOpened(_logger, _namedConnectionToUse, null);
            DbConnectionTracking.Track(connection, _activeDbConnectionTracker);

            return connection;
        }
        catch (Exception ex)
        {
            _logSecureConnectionFallback(_logger, _namedConnectionToUse, ex);

            // Fall back to default connection if secure resolution fails
            return await _defaultProvider.OpenConnectionAsync(cancellationToken);
        }
    }

    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);

        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // Delegate to default provider for retry logic
        return await _defaultProvider.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);
    }

    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        // Delegate to default provider for retry logic
        await _defaultProvider.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);
    }

    /// <summary>
    /// Checks if the secure connection is healthy and accessible.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if secure connection is healthy or not configured</returns>
    public async Task<bool> IsSecureConnectionHealthyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_namedConnectionToUse))
        {
            // No secure connection configured - always healthy
            return true;
        }

        try
        {
            return await _secureResolver.TestConnectionHealthAsync(_namedConnectionToUse, cancellationToken);
        }
        catch (Exception ex)
        {
            _logConnectionHealthTestFailure(_logger, _namedConnectionToUse, ex);
            return false;
        }
    }

    /// <summary>
    /// Gets the available secure connections for this provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available secure connection names</returns>
    public async Task<string[]> GetAvailableSecureConnectionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connections = await _secureResolver.GetAvailableConnectionsAsync(cancellationToken);
            return connections.ToArray();
        }
        catch (Exception ex)
        {
            _logRetrieveConnectionsFailure(_logger, ex);
            return Array.Empty<string>();
        }
    }
}
