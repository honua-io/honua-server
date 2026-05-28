// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;
namespace Honua.Server.Features.FileImport;

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

    public static bool IsValidSchemaName(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName) || schemaName.Length > 63)
        {
            return false;
        }

        return schemaName.All(c => char.IsLetterOrDigit(c) || c == '_') &&
               (char.IsLetter(schemaName[0]) || schemaName[0] == '_');
    }
}
