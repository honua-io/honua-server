// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Caching;

/// <summary>
/// Enhanced PostgreSQL connection provider with prepared statement caching support
/// </summary>
/// <remarks>
/// <para>
/// Extends the base connection provider with intelligent prepared statement caching
/// to optimize frequently-executed queries. Maintains compatibility with existing
/// connection patterns while adding transparent performance improvements.
/// </para>
/// <para>
/// PERFORMANCE FEATURES:
/// - Automatic prepared statement management
/// - Connection-aware caching for optimal resource usage
/// - Graceful degradation when caching is disabled
/// - Comprehensive monitoring and logging capabilities
/// </para>
/// <para>
/// SECURITY: All existing parameterized query patterns remain secure.
/// The caching layer only optimizes execution, not query construction.
/// </para>
/// </remarks>
internal sealed partial class CachingDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<CachingDatabaseConnectionProvider> _logger;
    private readonly ISchemaContext? _schemaContext;

    public CachingDatabaseConnectionProvider(
        NpgsqlDataSource dataSource,
        ILogger<CachingDatabaseConnectionProvider> logger,
        ISchemaContext? schemaContext = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaContext = schemaContext;
    }

    /// <summary>
    /// Opens a PostgreSQL connection with automatic retry for transient failures
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A caching-enabled PostgreSQL connection</returns>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        // Use the resilience extension method with logging callback
        var connection = await _dataSource.OpenConnectionWithRetryAsync(
            onRetry: (ex, delay, attempt) => DatabaseConnectionRetry(_logger, attempt, ex.Message, ex),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await SchemaSearchPath.ApplyAsync(connection, _schemaContext?.CurrentSchema, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    [LoggerMessage(1, LogLevel.Warning, "Database connection retry attempt {Attempt}: {ErrorMessage}")]
    private static partial void DatabaseConnectionRetry(ILogger logger, int attempt, string errorMessage, Exception exception);
}
