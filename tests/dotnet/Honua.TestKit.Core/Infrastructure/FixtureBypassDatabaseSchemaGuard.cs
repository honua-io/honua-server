// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit;

public sealed class FixtureBypassDatabaseSchemaGuard : IDatabaseSchemaGuard
{
    private FixtureBypassDatabaseSchemaGuard()
    {
    }

    public static FixtureBypassDatabaseSchemaGuard Instance { get; } = new();

    public Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task VerifyAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task VerifyConsistencyAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task VerifyRequirementAsync(
        DbConnection connection,
        DatabaseSchemaRequirement requirement,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
