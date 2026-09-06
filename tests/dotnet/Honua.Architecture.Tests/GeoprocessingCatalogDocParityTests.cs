// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards against silent drift between the built-in geoprocessing process catalog
/// (<c>BuiltInProcessCatalog</c>) and its public reference documentation
/// (<c>docs/reference/geoprocessing-operations.md</c>). Every registered process id
/// must be documented so adapters and operators can trust the page.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class GeoprocessingCatalogDocParityTests
{
    private const string DocRelativePath = "docs/reference/geoprocessing-operations.md";

    [Fact]
    public void EveryCatalogProcess_HasACanonicalExecutionClassification()
    {
        var processes = new BuiltInProcessCatalog().ListProcesses();

        processes.Should().HaveCount(98);
        processes.Should().NotContain(
            process => process.ExecutionKind == ProcessExecutionKind.Unclassified,
            "protocol adapters must not guess whether a catalog process is directly callable");
        processes.Count(process => process.ExecutionKind == ProcessExecutionKind.Job).Should().Be(80);
        processes.Count(process => process.ExecutionKind == ProcessExecutionKind.ProtocolOnly).Should().Be(6);
        processes.Count(process => process.ExecutionKind == ProcessExecutionKind.WorkflowOnly).Should().Be(12);
        processes.Should().NotContain(
            process => process.ExecutionKind == ProcessExecutionKind.Unavailable,
            "the catalog admits no advertised-but-unexecutable state: an operation is either callable "
            + "through a declared entry point or it is not in the catalog (#4409)");
    }

    /// <summary>
    /// Every advertised operation must declare at least one entry point, and the declared
    /// entry points must agree with the canonical execution classification. This is the
    /// catalog half of the entry-point contract; the advertisement half — that no surface
    /// offers an operation on an undeclared entry point — is enforced by
    /// <c>ProcessEntryPointAdvertisementTests</c>.
    /// </summary>
    [Fact]
    public void EveryCatalogProcess_DeclaresEntryPointsMatchingItsClassification()
    {
        foreach (var process in new BuiltInProcessCatalog().ListProcesses())
        {
            var expected = process.ExecutionKind switch
            {
                ProcessExecutionKind.Job => ProcessEntryPoints.Job | ProcessEntryPoints.Workflow,
                ProcessExecutionKind.ProtocolOnly => ProcessEntryPoints.Protocol,
                ProcessExecutionKind.WorkflowOnly => ProcessEntryPoints.Workflow,
                _ => ProcessEntryPoints.None
            };

            process.SupportedEntryPoints.Should().Be(
                expected,
                $"'{process.ProcessId}' is classified {process.ExecutionKind}, so its declared entry points must match");
            process.SupportedEntryPoints.Should().NotBe(
                ProcessEntryPoints.None,
                $"'{process.ProcessId}' is advertised in the catalog, so it must be callable somewhere");
        }
    }

    /// <summary>
    /// The reference page is a generated catalog document, so it must state the entry
    /// points per operation exactly as the catalog declares them — an operator reading the
    /// page has to be able to tell which surface will accept the operation.
    /// </summary>
    [Fact]
    public void EveryCatalogProcess_DeclaresItsEntryPointsInTheReferenceDoc()
    {
        var docLines = ReadDoc().Split('\n');

        foreach (var process in new BuiltInProcessCatalog().ListProcesses())
        {
            var row = docLines.FirstOrDefault(
                line => line.StartsWith($"| `{process.ProcessId}` |", StringComparison.Ordinal));
            row.Should().NotBeNull(
                $"{DocRelativePath} must carry a table row for '{process.ProcessId}'");

            var declared = ProcessExecutionEligibility.DescribeEntryPoints(process);
            var cells = row!.Split('|', StringSplitOptions.None);
            var entryPointCell = cells[^2].Trim();
            entryPointCell.Should().Be(
                string.Join(", ", declared),
                $"the Entry points column for '{process.ProcessId}' in {DocRelativePath} must match the "
                + "catalog declaration; regenerate the page from BuiltInProcessCatalog");
        }
    }

    [Fact]
    public void EveryCatalogProcessId_IsDocumentedInReference()
    {
        var docText = ReadDoc();

        var catalog = new BuiltInProcessCatalog();
        var processIds = catalog.ListProcesses().Select(p => p.ProcessId).ToList();

        processIds.Should().NotBeEmpty();

        // Each id is rendered in a Markdown table cell as `process.id`; require the
        // back-ticked token so a stray prose mention does not satisfy the check.
        var missing = processIds
            .Where(id => !docText.Contains($"`{id}`", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "every registered geoprocessing process id must be documented in "
            + $"{DocRelativePath}; regenerate it from BuiltInProcessCatalog. Missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Guards against the prose process count (and native-process fraction) drifting
    /// from the catalog, which is exactly what happened before honua-server#2355:
    /// the doc said "95 processes" (and "30 of the 95") while the catalog had grown
    /// to 96. Both numbers are pinned here so a future catalog change that isn't
    /// reflected in the doc fails the build instead of silently going stale.
    /// </summary>
    [Fact]
    public void DocProseCount_MatchesCatalogCount()
    {
        var docText = ReadDoc();

        var catalog = new BuiltInProcessCatalog();
        var processes = catalog.ListProcesses().ToList();
        var expectedTotal = processes.Count;
        var expectedNative = processes.Count(p => p.RuntimeProfile == RuntimeProfiles.Native);

        var totalMatch = Regex.Match(docText, @"catalog currently registers \*\*(?<count>\d+) processes\*\*");
        totalMatch.Success.Should().BeTrue(
            $"{DocRelativePath} should have a \"catalog currently registers **N processes**\" sentence to check against the catalog");
        int.Parse(totalMatch.Groups["count"].Value, CultureInfo.InvariantCulture).Should().Be(
            expectedTotal,
            $"the prose process count in {DocRelativePath} must match BuiltInProcessCatalog ({expectedTotal} processes)");

        var nativeMatch = Regex.Match(docText, @"(?<native>\d+) of the (?<total>\d+) processes are native");
        nativeMatch.Success.Should().BeTrue(
            $"{DocRelativePath} should have an \"N of the M processes are native\" sentence to check against the catalog");
        int.Parse(nativeMatch.Groups["total"].Value, CultureInfo.InvariantCulture).Should().Be(
            expectedTotal,
            $"the native-fraction denominator in {DocRelativePath} must match BuiltInProcessCatalog ({expectedTotal} processes)");
        int.Parse(nativeMatch.Groups["native"].Value, CultureInfo.InvariantCulture).Should().Be(
            expectedNative,
            $"the native-fraction numerator in {DocRelativePath} must match the catalog's native-profile process count ({expectedNative})");
    }

    private static string ReadDoc()
    {
        var docPath = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            DocRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(docPath).Should().BeTrue($"the geoprocessing reference doc should exist at {DocRelativePath}");

        return File.ReadAllText(docPath);
    }
}
