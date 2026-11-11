// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security;

namespace Honua.Import.FileImport;

internal static partial class ImportEndpoints
{
    internal static async Task<ImportProgress> EnrichImportReceiptAsync(
        IServiceProvider services,
        ImportProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress.Status != ImportStatus.Completed)
        {
            return progress with { Schema = null, PhysicalTableName = null, ConnectionId = null };
        }

        var connectionId = await FindImportConnectionIdAsync(services, cancellationToken).ConfigureAwait(false);
        const string warning = "No unique active database connection matches this import; register a matching connection before publishing.";
        return progress with
        {
            ConnectionId = connectionId,
            Warnings = connectionId is null && !progress.Warnings.Contains(warning, StringComparer.Ordinal)
                ? [.. progress.Warnings, warning]
                : progress.Warnings
        };
    }

    private static async Task<string?> FindImportConnectionIdAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = services.GetService<IDatabaseConnectionProvider>();
            var match = await CatalogConnectionDiscovery.FindUniqueAsync(
                services, provider?.GetConnectionString(), cancellationToken).ConfigureAwait(false);
            return match.Connection?.ConnectionId.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Connection discovery is optional; retain the physical receipt with an actionable warning.
            return null;
        }
    }
}
