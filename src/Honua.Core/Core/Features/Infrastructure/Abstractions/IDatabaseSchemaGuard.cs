// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface IDatabaseSchemaGuard
{
    Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default);

    Task VerifyAsync(DbConnection connection, CancellationToken cancellationToken = default);

    // The default ignores the qualified set and keeps the strict check, so a guard that does not model forward compatibility fails closed.
    Task VerifyAsync(
        DbConnection connection,
        IReadOnlySet<string> readerVerifiedMigrations,
        CancellationToken cancellationToken = default)
        => VerifyAsync(connection, cancellationToken);

    Task VerifyConsistencyAsync(DbConnection connection, CancellationToken cancellationToken = default);

    // The default ignores the qualified set and keeps the strict check, so a guard that does not model forward compatibility fails closed.
    Task VerifyConsistencyAsync(
        DbConnection connection,
        IReadOnlySet<string> readerVerifiedMigrations,
        CancellationToken cancellationToken = default)
        => VerifyConsistencyAsync(connection, cancellationToken);

    // Only an empty journal may adopt seed tables, and a guard that does not model adoption fails closed.
    Task VerifyFirstMigrationAdoptionAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => VerifyConsistencyAsync(connection, cancellationToken);

    Task<string?> TryAdoptMatchingRasterBaselineAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    Task VerifyRequirementAsync(
        DbConnection connection,
        DatabaseSchemaRequirement requirement,
        CancellationToken cancellationToken = default);
}
