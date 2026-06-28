// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
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
    public void EveryCatalogProcessId_IsDocumentedInReference()
    {
        var docPath = Path.Combine(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            DocRelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(docPath).Should().BeTrue($"the geoprocessing reference doc should exist at {DocRelativePath}");

        var docText = File.ReadAllText(docPath);

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
}
