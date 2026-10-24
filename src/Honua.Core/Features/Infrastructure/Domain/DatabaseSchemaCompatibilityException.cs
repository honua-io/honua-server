// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
        : this(unknownMigrations, null)
    {
    }

    /// <summary>
    /// Creates a failure for unrecognized applied migrations, naming why any recorded
    /// forward-compatibility declaration for them was refused (#6477).
    /// </summary>
    /// <param name="unknownMigrations">Applied migration identities this reader cannot qualify.</param>
    /// <param name="declarationRefusals">Refusal reason per refused forward-compatibility declaration.</param>
    public DatabaseSchemaCompatibilityException(
        IReadOnlyList<string> unknownMigrations,
        IReadOnlyDictionary<string, string>? declarationRefusals)
        : base($"{ErrorCode}: this server cannot verify compatibility with applied migration(s): " +
            $"{string.Join(", ", unknownMigrations)}. Start a compatible server or restore the database " +
            "backup matching this revision. Application rollback does not reverse database migrations." +
            DescribeRefusals(declarationRefusals))
    {
        UnknownMigrations = unknownMigrations.ToArray();
        DeclarationRefusals = declarationRefusals is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(declarationRefusals, StringComparer.Ordinal);
    }

    /// <summary>Applied migration identities absent from this reader's manifest.</summary>
    public IReadOnlyList<string> UnknownMigrations { get; }

    /// <summary>
    /// Unknown migrations whose recorded forward-compatibility declaration this reader refused, with
    /// the reason. Empty when no declaration was recorded for any of them.
    /// </summary>
    public IReadOnlyDictionary<string, string> DeclarationRefusals { get; }

    private static string DescribeRefusals(IReadOnlyDictionary<string, string>? refusals)
        => refusals is null || refusals.Count == 0
            ? string.Empty
            : " Forward-compatibility declaration(s) refused: " + string.Join("; ", refusals
                .OrderBy(refusal => refusal.Key, StringComparer.Ordinal)
                .Select(refusal => $"{refusal.Key} ({refusal.Value})")) + ".";
}
