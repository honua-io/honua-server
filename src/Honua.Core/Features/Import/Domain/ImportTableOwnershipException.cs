// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>A destructive import refused a target or staging table not owned by the importer.</summary>
public sealed class ImportTableOwnershipException(string schema, string table)
    : InvalidOperationException(CreateSafeMessage($"{schema}.{table}"))
{
    /// <summary>Stable conflict code shared by synchronous responses and terminal job progress.</summary>
    public const string Code = "IMPORT_TARGET_NOT_OWNED";

    /// <summary>Formats a client-safe conflict message using the requested target, never provider diagnostics.</summary>
    public static string CreateSafeMessage(string target)
        => $"{Code}: Table '{target}' is not owned by the importer; it was not modified. Choose a new target or ask the database administrator to explicitly adopt a verified legacy import.";
}
