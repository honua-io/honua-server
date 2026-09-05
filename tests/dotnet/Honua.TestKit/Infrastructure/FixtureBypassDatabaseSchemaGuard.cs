// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit;

/// <summary>
/// Explicit test-fixture seam for schemas provisioned directly by test seed infrastructure rather
/// than the production DbUp journal. This type lives only in Honua.TestKit; no production project
/// references that assembly.
/// </summary>
internal sealed class FixtureBypassDatabaseSchemaGuard : IDatabaseSchemaGuard
{
    private FixtureBypassDatabaseSchemaGuard()
    {
    }

    /// <summary>Gets the single fixture-only bypass instance.</summary>
    internal static FixtureBypassDatabaseSchemaGuard Instance { get; } = new();

    /// <inheritdoc />
    public Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task VerifyAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task VerifyConsistencyAsync(DbConnection connection, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task VerifyRequirementAsync(
        DbConnection connection,
        DatabaseSchemaRequirement requirement,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
