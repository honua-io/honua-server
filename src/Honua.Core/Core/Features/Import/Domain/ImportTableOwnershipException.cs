// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

public sealed class ImportTableOwnershipException(string schema, string table)
    : InvalidOperationException(CreateSafeMessage($"{schema}.{table}"))
{
    public const string Code = "IMPORT_TARGET_NOT_OWNED";

    // The conflict message uses the requested target and never provider diagnostics.
    public static string CreateSafeMessage(string target)
        => $"{Code}: Table '{target}' is not owned by the importer; it was not modified. Choose a new target or ask the database administrator to explicitly adopt a verified legacy import.";
}
