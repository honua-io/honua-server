// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// The database contains migration identities this reader has not qualified. Application rollback
/// must stop before serving; deploy a compatible reader or restore its matching database backup.
/// </summary>
public sealed class DatabaseSchemaCompatibilityException : InvalidOperationException
{
    /// <summary>Stable startup failure code for deployment and release receipts.</summary>
    public const string ErrorCode = "schema_reader_incompatible";

    /// <summary>Creates a failure for unrecognized applied migrations.</summary>
    public DatabaseSchemaCompatibilityException(IReadOnlyList<string> unknownMigrations)
        : base($"{ErrorCode}: this server cannot verify compatibility with applied migration(s): " +
            $"{string.Join(", ", unknownMigrations)}. Start a compatible server or restore the database " +
            "backup matching this revision. Application rollback does not reverse database migrations.")
    {
        UnknownMigrations = unknownMigrations.ToArray();
    }

    /// <summary>Applied migration identities absent from this reader's manifest.</summary>
    public IReadOnlyList<string> UnknownMigrations { get; }
}
