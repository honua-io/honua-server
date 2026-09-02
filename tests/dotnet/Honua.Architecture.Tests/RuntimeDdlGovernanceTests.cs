// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Inventories production C# that can issue DDL. Core Honua schema evolution belongs only in the
/// numbered migration roots; the allowlist is restricted to bounded generated/output targets,
/// attempt-scoped routing shadows, and the audited retention-maintenance transaction.
/// </summary>
[Trait("Category", "Architecture")]
public sealed partial class RuntimeDdlGovernanceTests
{
    private static readonly IReadOnlyDictionary<string, RuntimeDdlOwner> _allowlist =
        new Dictionary<string, RuntimeDdlOwner>(StringComparer.Ordinal)
        {
            ["src/Honua.Db/Postgres/Features/Migration/OgcWfsImportService.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, 4, "OgcWfsImportService owns rollback/drop cleanup for its generated target table."),
            ["src/Honua.Db/Postgres/Features/Migration/GeoservicesImportService.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, 1, "GeoservicesImportService owns the generated target index in its import transaction."),
            ["src/Honua.Db/Postgres/Features/Migration/GeoservicesImportService.ImportSteps.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, 3, "GeoservicesImportService owns create/drop compensation for its generated import table."),
            ["src/Honua.Db/Postgres/Features/Migration/PostgresMigrationCatalogWriter.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, 2, "PostgresMigrationCatalogWriter owns attempt-scoped target creation and catalog rollback."),
            ["src/Honua.Db/Postgres/Features/Geoprocessing/PostgresHonuaLayerSink.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, 2, "PostgresHonuaLayerSink owns the generated job-output table transaction."),
            ["src/Honua.Geocoding/Features/Geocoding/ReferenceDataImport/GeocoderReferenceDataImportService.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, 3, "GeocoderReferenceDataImportService owns its replaceable reference-data target and indexes."),
            ["src/Honua.Db/Postgres/Features/Migration/PostgresOgcApiFeaturesCollectionSink.cs"] =
                new(RuntimeDdlCategory.ExternalSinkTarget, 4, "PostgresOgcApiFeaturesCollectionSink owns its destination schema/table and import-scope cleanup."),
            ["src/Honua.Geoprocessing/Features/Geoprocessing/Execution/ExternalPostgisSinkExecutor.cs"] =
                new(RuntimeDdlCategory.ExternalSinkTarget, 2, "ExternalPostgisSinkExecutor owns the caller-selected external sink table transaction."),
            ["src/Honua.Io/Features/Export/Writers/GeoPackageExportWriter.cs"] =
                new(RuntimeDdlCategory.ExportArtifact, 4, "GeoPackageExportWriter owns and disposes the standalone export artifact."),
            ["src/Honua.Routing/Features/Routing/Providers/NetworkTopologyShadowTopologyBuilder.cs"] =
                new(RuntimeDdlCategory.RoutingShadow, 10, "NetworkTopologyShadowTopologyBuilder owns attempt-scoped shadow tables and failure cleanup."),
            ["src/Honua.Routing/Features/Routing/Providers/PostgresNetworkTopologyRebuildStore.cs"] =
                new(RuntimeDdlCategory.RoutingShadow, 2, "PostgresNetworkTopologyRebuildStore owns terminal cleanup of attempt-scoped shadow tables."),
            ["src/Honua.Routing/Features/Routing/Providers/PostgresNetworkTopologyPromotionStore.cs"] =
                new(RuntimeDdlCategory.RoutingShadow, 2, "PostgresNetworkTopologyPromotionStore owns trigger suspension/re-enable in the promotion transaction."),
            ["src/Honua.Db/Postgres/Features/AuditLog/PostgresAuditLogRetentionPruner.cs"] =
                new(RuntimeDdlCategory.RetentionMaintenance, 2, "PostgresAuditLogRetentionPruner owns rule suspension/re-enable in its audited retention transaction."),
        };

    [ArchitectureTest]
    public void ProductionRuntimeDdl_ShouldBeLimitedToExplicitlyOwnedNonCoreTargets()
    {
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        var sourceRoot = Path.Join(projectRoot, "src");
        var observed = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = Path.GetRelativePath(projectRoot, path).Replace('\\', '/'),
                Count = CountRuntimeDdlStrings(path),
            })
            .Where(entry => entry.Count > 0)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Path, entry => entry.Count, StringComparer.Ordinal);

        var allowed = _allowlist
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value.ExpectedOccurrenceCount, StringComparer.Ordinal);
        observed.Should().Equal(
            allowed,
            "every production runtime DDL occurrence must be registered with a bounded category and transactional/cleanup owner; core-schema DDL belongs only in numbered SQL migrations");

        _allowlist.Values.Should().OnlyContain(
            entry => Enum.IsDefined(entry.Category) &&
                     entry.ExpectedOccurrenceCount > 0 &&
                     !string.IsNullOrWhiteSpace(entry.Owner),
            "every runtime DDL exception must name its permitted category, positive occurrence count, and transactional/cleanup owner");
    }

    private static int CountRuntimeDdlStrings(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
        return tree.GetRoot()
            .DescendantTokens()
            .Sum(token => RuntimeDdlPattern().Count(token.ValueText));
    }

    private static string FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Honua.sln from the current test directory.");
    }

    [GeneratedRegex(
        @"(?:^|[;'\r\n])\s*(?:CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?(?:TABLE|SCHEMA|INDEX|VIEW|TYPE|DOMAIN|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|RULE|EXTENSION|POLICY)|ALTER\s+(?:TABLE|SCHEMA|INDEX|VIEW|TYPE|DOMAIN|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|RULE|EXTENSION|POLICY)|DROP\s+(?:TABLE|SCHEMA|INDEX|VIEW|TYPE|DOMAIN|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|RULE|EXTENSION|POLICY)|TRUNCATE\s+TABLE)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeDdlPattern();

    private sealed record RuntimeDdlOwner(
        RuntimeDdlCategory Category,
        int ExpectedOccurrenceCount,
        string Owner);

    private enum RuntimeDdlCategory
    {
        GeneratedImportTarget,
        RoutingShadow,
        ExternalSinkTarget,
        ExportArtifact,
        RetentionMaintenance,
    }
}
