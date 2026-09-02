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
                new(RuntimeDdlCategory.GeneratedImportTarget, "OgcWfsImportService owns rollback/drop cleanup for its generated target table."),
            ["src/Honua.Db/Postgres/Features/Migration/GeoservicesImportService.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, "GeoservicesImportService owns the generated target index in its import transaction."),
            ["src/Honua.Db/Postgres/Features/Migration/GeoservicesImportService.ImportSteps.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, "GeoservicesImportService owns create/drop compensation for its generated import table."),
            ["src/Honua.Db/Postgres/Features/Migration/PostgresMigrationCatalogWriter.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, "PostgresMigrationCatalogWriter owns attempt-scoped target creation and catalog rollback."),
            ["src/Honua.Db/Postgres/Features/Geoprocessing/PostgresHonuaLayerSink.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, "PostgresHonuaLayerSink owns the generated job-output table transaction."),
            ["src/Honua.Geocoding/Features/Geocoding/ReferenceDataImport/GeocoderReferenceDataImportService.cs"] =
                new(RuntimeDdlCategory.GeneratedImportTarget, "GeocoderReferenceDataImportService owns its replaceable reference-data target and indexes."),
            ["src/Honua.Db/Postgres/Features/Migration/PostgresOgcApiFeaturesCollectionSink.cs"] =
                new(RuntimeDdlCategory.ExternalSinkTarget, "PostgresOgcApiFeaturesCollectionSink owns its destination schema/table and import-scope cleanup."),
            ["src/Honua.Geoprocessing/Features/Geoprocessing/Execution/ExternalPostgisSinkExecutor.cs"] =
                new(RuntimeDdlCategory.ExternalSinkTarget, "ExternalPostgisSinkExecutor owns the caller-selected external sink table transaction."),
            ["src/Honua.Io/Features/Export/Writers/GeoPackageExportWriter.cs"] =
                new(RuntimeDdlCategory.ExportArtifact, "GeoPackageExportWriter owns and disposes the standalone export artifact."),
            ["src/Honua.Routing/Features/Routing/Providers/NetworkTopologyShadowTopologyBuilder.cs"] =
                new(RuntimeDdlCategory.RoutingShadow, "NetworkTopologyShadowTopologyBuilder owns attempt-scoped shadow tables and failure cleanup."),
            ["src/Honua.Routing/Features/Routing/Providers/PostgresNetworkTopologyRebuildStore.cs"] =
                new(RuntimeDdlCategory.RoutingShadow, "PostgresNetworkTopologyRebuildStore owns terminal cleanup of attempt-scoped shadow tables."),
            ["src/Honua.Routing/Features/Routing/Providers/PostgresNetworkTopologyPromotionStore.cs"] =
                new(RuntimeDdlCategory.RoutingShadow, "PostgresNetworkTopologyPromotionStore owns trigger suspension/re-enable in the promotion transaction."),
            ["src/Honua.Db/Postgres/Features/AuditLog/PostgresAuditLogRetentionPruner.cs"] =
                new(RuntimeDdlCategory.RetentionMaintenance, "PostgresAuditLogRetentionPruner owns rule suspension/re-enable in its audited retention transaction."),
        };

    [ArchitectureTest]
    public void ProductionRuntimeDdl_ShouldBeLimitedToExplicitlyOwnedNonCoreTargets()
    {
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        var sourceRoot = Path.Combine(projectRoot, "src");
        var observed = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(ContainsRuntimeDdlString)
            .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var allowed = _allowlist.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        observed.Should().Equal(
            allowed,
            "every production runtime DDL string must be registered with a bounded category and transactional/cleanup owner; core-schema DDL belongs only in numbered SQL migrations");

        _allowlist.Values.Should().OnlyContain(
            entry => Enum.IsDefined(entry.Category) && !string.IsNullOrWhiteSpace(entry.Owner),
            "every runtime DDL exception must name its permitted category and transactional/cleanup owner");
    }

    private static bool ContainsRuntimeDdlString(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
        return tree.GetRoot()
            .DescendantTokens()
            .Any(token => RuntimeDdlPattern().IsMatch(token.ValueText));
    }

    private static string FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Honua.sln from the current test directory.");
    }

    [GeneratedRegex(
        @"\b(?:CREATE\s+(?:TABLE|SCHEMA|INDEX)|ALTER\s+TABLE|DROP\s+(?:TABLE|SCHEMA)|TRUNCATE\s+TABLE)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeDdlPattern();

    private sealed record RuntimeDdlOwner(RuntimeDdlCategory Category, string Owner);

    private enum RuntimeDdlCategory
    {
        GeneratedImportTarget,
        RoutingShadow,
        ExternalSinkTarget,
        ExportArtifact,
        RetentionMaintenance,
    }
}
