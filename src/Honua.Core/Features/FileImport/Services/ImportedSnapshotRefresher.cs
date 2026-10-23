// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Refreshes the canonical published-feature snapshot after a background import, shared by the
/// in-process runner (<see cref="UniversalImportJobService"/>) and the durable
/// <c>FileImport</c> execution-job executor so both paths keep tiles consistent with the
/// imported data (#22, honua-server#1628).
/// </summary>
internal static class ImportedSnapshotRefresher
{
    /// <summary>
    /// Refreshes every physical table name the importer may have used for
    /// <paramref name="tableName"/>. Best-effort: failures are reported to
    /// <paramref name="onError"/> and never thrown, except cancellation.
    /// </summary>
    public static async Task RefreshAsync(
        IServiceProvider services,
        string? targetSchema,
        string tableName,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(onError);

        if (services.GetService<ILayerPublishingService>() is not { } publishingService ||
            services.GetService<IDatabaseConnectionProvider>() is not { } connectionProvider)
        {
            return;
        }

        try
        {
            var connectionString = connectionProvider.GetConnectionString();
            foreach (var physicalTableName in ResolvePhysicalImportedTableNames(tableName))
            {
                try
                {
                    await publishingService.RefreshMaterializedFeaturesForSourceTableAsync(
                        connectionString,
                        targetSchema,
                        physicalTableName,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A legacy 41-63 character physical name may not exist; continue
                    // to the current hashed name so published snapshots remain fresh.
                    onError(ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onError(ex);
        }
    }

    /// <summary>
    /// The physical table names the importer stores a logical file-import target under.
    /// </summary>
    internal static IReadOnlyList<string> ResolvePhysicalImportedTableNames(string tableName)
    {
        var sanitized = Regex.Replace(tableName, "[^a-zA-Z0-9_]", "_");
        var physical = "imported_" + sanitized.ToLowerInvariant();
        if (physical.Length <= 40)
        {
            return [physical];
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physical)))[..12];
        var hashed = $"{physical[..(40 - hash.Length - 1)]}_{hash}";
        // Imports before the length-limit fix used the unsuffixed name through
        // PostgreSQL's 63-character identifier limit. Refresh both candidates so
        // those published layers are not stranded after an upgrade.
        return physical.Length <= 63 ? [physical, hashed] : [hashed];
    }
}
