// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

// Serving must stop when the database contains migration identities this reader has not qualified.
public sealed class DatabaseSchemaCompatibilityException : InvalidOperationException
{
    public const string ErrorCode = "schema_reader_incompatible";

    public DatabaseSchemaCompatibilityException(IReadOnlyList<string> unknownMigrations)
        : this(unknownMigrations, null)
    {
    }

    public DatabaseSchemaCompatibilityException(
        IReadOnlyList<string> unknownMigrations,
        IReadOnlyDictionary<string, string>? declarationRefusals)
        : base($"{ErrorCode}: this server cannot verify compatibility with applied migration(s): " +
            $"{string.Join(", ", unknownMigrations)}. Start a compatible server or restore the database " +
            "backup matching this revision. Application rollback does not reverse database migrations. " +
            "See the Rollback boundaries table in Upgrade and roll back." +
            DescribeRefusals(declarationRefusals))
    {
        UnknownMigrations = unknownMigrations.ToArray();
        DeclarationRefusals = declarationRefusals is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(declarationRefusals, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> UnknownMigrations { get; }

    public IReadOnlyDictionary<string, string> DeclarationRefusals { get; }

    private static string DescribeRefusals(IReadOnlyDictionary<string, string>? refusals)
        => refusals is null || refusals.Count == 0
            ? string.Empty
            : " Forward-compatibility declaration(s) refused: " + string.Join("; ", refusals
                .OrderBy(refusal => refusal.Key, StringComparer.Ordinal)
                .Select(refusal => $"{refusal.Key} ({refusal.Value})")) + ".";
}
