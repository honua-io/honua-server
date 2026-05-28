// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Builds deterministic migration fidelity matrix rollups from per-source classification rows.
/// </summary>
public static class MigrationFidelityMatrixBuilder
{
    /// <summary>
    /// Builds a matrix from source fidelity classifications.
    /// </summary>
    /// <param name="classifications">Source fidelity classification records.</param>
    /// <param name="identityRemaps">Optional source-to-target identity remaps used to enrich matrix cells.</param>
    /// <returns>A deterministic matrix, or <c>null</c> when no classifications are present.</returns>
    public static MigrationFidelityMatrix? Build(
        IEnumerable<MigrationFidelityClassificationRecord> classifications,
        IEnumerable<MigrationManifestIdentityRemap>? identityRemaps = null)
    {
        ArgumentNullException.ThrowIfNull(classifications);

        var orderedClassifications = classifications
            .Where(static record => !string.IsNullOrWhiteSpace(record.Category))
            .OrderBy(static record => record.Category, StringComparer.Ordinal)
            .ThenBy(static record => NormalizeAutomationStatus(record.AutomationStatus), StringComparer.Ordinal)
            .ThenBy(static record => record.SourceId, StringComparer.Ordinal)
            .ThenBy(static record => record.Id, StringComparer.Ordinal)
            .ToArray();

        if (orderedClassifications.Length == 0)
        {
            return null;
        }

        var remapsBySourceId = (identityRemaps ?? [])
            .Where(static remap => !string.IsNullOrWhiteSpace(remap.SourceId))
            .GroupBy(static remap => remap.SourceId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static remap => remap.TargetId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        var cells = orderedClassifications
            .GroupBy(
                static record => new MatrixCellKey(record.Category.Trim(), NormalizeAutomationStatus(record.AutomationStatus)),
                MatrixCellKeyComparer.Instance)
            .Select(group => BuildCell(group.Key, group, remapsBySourceId))
            .OrderBy(static cell => cell.Category, StringComparer.Ordinal)
            .ThenBy(static cell => cell.AutomationStatus, StringComparer.Ordinal)
            .ToArray();

        return new MigrationFidelityMatrix
        {
            Summary = new MigrationFidelityMatrixSummary
            {
                TotalCount = orderedClassifications.Length,
                AutomatedCount = CountStatus(orderedClassifications, MigrationFidelityAutomationStatuses.Automated),
                AssistedCount = CountStatus(orderedClassifications, MigrationFidelityAutomationStatuses.Assisted),
                ManualReviewCount = CountStatus(orderedClassifications, MigrationFidelityAutomationStatuses.ManualReview),
                UnsupportedCount = CountStatus(orderedClassifications, MigrationFidelityAutomationStatuses.Unsupported)
            },
            Cells = cells
        };
    }

    private static MigrationFidelityMatrixCell BuildCell(
        MatrixCellKey key,
        IEnumerable<MigrationFidelityClassificationRecord> records,
        IReadOnlyDictionary<string, MigrationManifestIdentityRemap[]> remapsBySourceId)
    {
        var materialized = records.ToArray();
        var targetIds = materialized
            .SelectMany(record => GetTargetIds(record, remapsBySourceId));

        return new MigrationFidelityMatrixCell
        {
            Category = key.Category,
            AutomationStatus = key.AutomationStatus,
            Count = materialized.Length,
            Kinds = Order(materialized.Select(static record => record.Kind)),
            SourceIds = Order(materialized.Select(static record => record.SourceId)),
            Codes = Order(materialized.Select(static record => record.Code)),
            TargetIds = Order(targetIds),
            RelatedIds = Order(materialized.SelectMany(static record => record.RelatedIds)),
            ManualSteps = Order(materialized.SelectMany(static record => record.ManualSteps))
        };
    }

    private static IEnumerable<string> GetTargetIds(
        MigrationFidelityClassificationRecord record,
        IReadOnlyDictionary<string, MigrationManifestIdentityRemap[]> remapsBySourceId)
    {
        if (!string.IsNullOrWhiteSpace(record.TargetId))
        {
            yield return record.TargetId;
        }

        if (!remapsBySourceId.TryGetValue(record.SourceId, out var remaps))
        {
            yield break;
        }

        foreach (var remap in remaps)
        {
            yield return remap.TargetId;
        }
    }

    private static int CountStatus(
        IEnumerable<MigrationFidelityClassificationRecord> records,
        string automationStatus)
        => records.Count(record => string.Equals(
            NormalizeAutomationStatus(record.AutomationStatus),
            automationStatus,
            StringComparison.Ordinal));

    private static string NormalizeAutomationStatus(string? automationStatus)
        => automationStatus switch
        {
            MigrationFidelityAutomationStatuses.Automated => MigrationFidelityAutomationStatuses.Automated,
            MigrationFidelityAutomationStatuses.Assisted => MigrationFidelityAutomationStatuses.Assisted,
            MigrationFidelityAutomationStatuses.ManualReview => MigrationFidelityAutomationStatuses.ManualReview,
            MigrationFidelityAutomationStatuses.Unsupported => MigrationFidelityAutomationStatuses.Unsupported,
            _ => MigrationFidelityAutomationStatuses.ManualReview
        };

    private static string[] Order(IEnumerable<string?> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private sealed record MatrixCellKey(string Category, string AutomationStatus);

    private sealed class MatrixCellKeyComparer : IEqualityComparer<MatrixCellKey>
    {
        public static readonly MatrixCellKeyComparer Instance = new();

        public bool Equals(MatrixCellKey? x, MatrixCellKey? y)
            => x is not null &&
               y is not null &&
               string.Equals(x.Category, y.Category, StringComparison.Ordinal) &&
               string.Equals(x.AutomationStatus, y.AutomationStatus, StringComparison.Ordinal);

        public int GetHashCode(MatrixCellKey obj)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Category),
                StringComparer.Ordinal.GetHashCode(obj.AutomationStatus));
    }
}
