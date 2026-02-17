// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Import;

/// <summary>
/// Shared validation helpers for import endpoints.
/// </summary>
internal static class ImportValidationHelpers
{
    public static bool IsValidTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Length > 63)
        {
            return false;
        }

        return tableName.All(c => char.IsLetterOrDigit(c) || c == '_') &&
               char.IsLetter(tableName[0]);
    }
}
