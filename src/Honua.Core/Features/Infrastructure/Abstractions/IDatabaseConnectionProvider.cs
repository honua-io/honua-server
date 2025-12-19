// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Provides database connections with built-in resilience and reliability
/// </summary>
/// <remarks>
/// This abstraction hides infrastructure concerns (connection pooling, retry policies, etc.)
/// from domain logic while providing reliable database connectivity.
/// </remarks>
public interface IDatabaseConnectionProvider
{
    /// <summary>
    /// Opens a database connection with automatic retry for transient failures
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open database connection</returns>
    /// <remarks>
    /// The returned connection includes:
    /// - Automatic retry for transient connection errors
    /// - Connection pooling and resource management
    /// - Structured logging for retry attempts
    /// - Proper error handling and cancellation support
    /// </remarks>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}