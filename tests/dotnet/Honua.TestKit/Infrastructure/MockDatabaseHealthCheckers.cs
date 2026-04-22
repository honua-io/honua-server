// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Mock database health checker that always returns healthy.
/// </summary>
public sealed class MockHealthyDatabaseChecker : IDatabaseHealthChecker
{
    /// <inheritdoc />
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

/// <summary>
/// Mock database health checker that always returns unhealthy.
/// </summary>
public sealed class MockUnhealthyDatabaseChecker : IDatabaseHealthChecker
{
    /// <inheritdoc />
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}

/// <summary>
/// Mock database health checker that throws a deterministic exception.
/// </summary>
public sealed class MockExceptionDatabaseChecker : IDatabaseHealthChecker
{
    /// <inheritdoc />
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database connection failed");
    }
}

/// <summary>
/// Mock database health checker that respects cancellation tokens.
/// </summary>
public sealed class MockCancellationDatabaseChecker : IDatabaseHealthChecker
{
    /// <inheritdoc />
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
