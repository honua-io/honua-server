// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;

namespace Honua.TestKit.Infrastructure;

public sealed class MockHealthyDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

public sealed class MockUnhealthyDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}

public sealed class MockExceptionDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database connection failed");
    }
}

public sealed class MockCancellationDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
