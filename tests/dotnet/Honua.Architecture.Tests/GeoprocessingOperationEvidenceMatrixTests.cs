// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the whole-catalog geoprocessing execution-evidence denominator adopted
/// for 2026.1. A catalog addition cannot land without an explicit evidence verdict.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class GeoprocessingOperationEvidenceMatrixTests
{
    private const string ManifestRelativePath = "certification/gp-operation-matrix.v1.json";

    /// <summary>
    /// Source files whose content decides whether a 'proven' verdict still applies.
    /// honua-server#4408: pinned by content digest (<see cref="ComputeCatalogSourceContentDigest"/>)
    /// rather than by a recorded git revision, so an executor change cannot silently
    /// outlive the audit that certified it — the guard fails loudly instead of trusting
    /// a <c>catalogSourceRevision</c> string nothing ever re-checks against the tree.
    /// </summary>
    private static readonly string[] CatalogSourceRoots =
    [
        "src/Honua.Geoprocessing/Features/Geoprocessing/BuiltInProcessCatalog.cs",
        "src/Honua.Geoprocessing/Features/Geoprocessing/ProcessExecutionCapabilityCatalog.cs",
        "src/Honua.Geoprocessing/Features/Geoprocessing/ProcessDestructiveClassifier.cs",
        "src/Honua.Geoprocessing/Features/Geoprocessing/GeoprocessingServiceCollectionExtensions.cs",
        "src/Honua.Geoprocessing/Features/Geoprocessing/Execution",
    ];

    [Fact]
    public void ManifestProcessIds_ExactlyMatchBuiltInCatalog()
    {
        using var manifest = ReadManifest();
        var manifestIds = manifest.RootElement.GetProperty("operations")
            .EnumerateArray()
            .Select(row => row.GetProperty("processId").GetString())
            .ToList();
        var catalogIds = new BuiltInProcessCatalog().ListProcesses()
            .Select(process => process.ProcessId)
            .ToList();

        manifestIds.Should().NotContainNulls();
        manifestIds.Should().OnlyHaveUniqueItems("each catalog process must have exactly one matrix verdict");
        manifestIds.Should().BeEquivalentTo(
            catalogIds,
            "a BuiltInProcessCatalog addition or removal must update the committed per-operation matrix");
    }

    [Fact]
    public void ManifestRows_HaveAuditableEvidenceOrOneConcreteGapIssue()
    {
        using var manifest = ReadManifest();
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var issueUrls = new List<string>();

        foreach (var row in manifest.RootElement.GetProperty("operations").EnumerateArray())
        {
            var processId = RequiredString(row, "processId");
            var status = RequiredString(row, "status");
            status.Should().BeOneOf("proven", "partially-proven", "unproven");

            var evidence = row.GetProperty("evidence").EnumerateArray().ToList();
            if (status == "proven")
            {
                evidence.Should().NotBeEmpty($"proven operation '{processId}' needs execution-content evidence");
                row.TryGetProperty("gap", out _).Should().BeFalse(
                    $"proven operation '{processId}' cannot retain an unresolved gap");
            }
            else
            {
                if (status == "partially-proven")
                {
                    evidence.Should().NotBeEmpty(
                        $"partially-proven operation '{processId}' must identify the evidence that falls short");
                }
                else
                {
                    evidence.Should().BeEmpty($"unproven operation '{processId}' cannot claim execution evidence");
                }

                row.TryGetProperty("gap", out var gap).Should().BeTrue(
                    $"{status} operation '{processId}' needs one concrete follow-up issue");
                RequiredString(gap, "missing").Should().NotBeNullOrWhiteSpace();
                var issue = RequiredString(gap, "issue");
                issue.Should().MatchRegex(@"^https://github\.com/honua-io/honua-server/issues/\d+$");
                issueUrls.Add(issue);
            }

            foreach (var receipt in evidence)
            {
                var relativePath = RequiredString(receipt, "path");
                var testName = RequiredString(receipt, "test");
                RequiredString(receipt, "assertion").Should().NotBeNullOrWhiteSpace();
                var expectedBodyDigest = RequiredString(receipt, "bodyDigest");

                var evidencePath = ArchitectureTestHelpers.CombinePath(
                    repositoryRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(evidencePath).Should().BeTrue(
                    $"evidence path for '{processId}' should exist: {relativePath}");

                var testBody = ResolveTestMethodBody(evidencePath, testName);
                testBody.Should().NotBeNull(
                    $"evidence test '{testName}' for '{processId}' should remain present in {relativePath}");
                ComputeDigest(testBody!).Should().Be(
                    expectedBodyDigest,
                    $"the body of evidence test '{testName}' for '{processId}' ({relativePath}) no longer "
                    + "matches the digest recorded when it was audited; a substring check on the test name "
                    + "alone would still pass with every assertion deleted or replaced, so this pins the "
                    + "actual method content — re-verify the test still proves the operation and update "
                    + "'bodyDigest' in the manifest row");
            }
        }

        issueUrls.Should().OnlyHaveUniqueItems(
            "partial and unproven operations require one-issue-per-operation follow-up");
    }

    /// <summary>
    /// The 2026-09-06 entry-point ruling (#4409): GA is defined per entry point, so a
    /// verdict is only meaningful together with the entry point it was proved through.
    /// A row that claims <c>proven</c> through an entry point the catalog does not declare
    /// for that operation is not a proof of the shipped capability — it is a proof of a
    /// path callers cannot take — so the gate refuses it.
    /// </summary>
    [Fact]
    public void ManifestRows_ProveTheirVerdictThroughADeclaredEntryPoint()
    {
        using var manifest = ReadManifest();
        var catalog = new BuiltInProcessCatalog();

        var definitions = manifest.RootElement.GetProperty("entryPointDefinitions");
        foreach (var entryPoint in new[] { "job", "protocol", "workflow" })
        {
            RequiredString(definitions, entryPoint).Should().NotBeNullOrWhiteSpace(
                $"the matrix must define what proving an operation through the '{entryPoint}' entry point means");
        }

        foreach (var row in manifest.RootElement.GetProperty("operations").EnumerateArray())
        {
            var processId = RequiredString(row, "processId");
            var entryPoint = RequiredString(row, "entryPoint");
            entryPoint.Should().BeOneOf("job", "protocol", "workflow");

            var definition = catalog.GetProcess(processId);
            definition.Should().NotBeNull($"matrix row '{processId}' must name a catalog operation");

            var declared = ProcessExecutionEligibility.DescribeEntryPoints(definition!);
            declared.Should().NotBeEmpty(
                $"catalog operation '{processId}' is advertised, so it must declare at least one callable "
                + "entry point; there is no advertised-but-unexecutable state");

            var status = RequiredString(row, "status");
            if (status == "proven")
            {
                declared.Should().Contain(
                    entryPoint,
                    $"proven operation '{processId}' claims a proof through the '{entryPoint}' entry point, "
                    + $"which the catalog does not declare for it (declared: {string.Join(", ", declared)}); "
                    + "downgrade the row to partially-proven or prove it through a declared entry point");
            }
            else
            {
                declared.Should().Contain(
                    entryPoint,
                    $"{status} operation '{processId}' must name a declared entry point as the one its missing "
                    + $"proof has to go through (declared: {string.Join(", ", declared)})");
            }
        }
    }

    [Fact]
    public void ManifestSummaryAndSharedRuntimeReferences_MatchRows()
    {
        using var manifest = ReadManifest();
        var root = manifest.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        RequiredString(root, "catalogVersion").Should().Be(BuiltInProcessCatalog.CatalogVersion);

        var operations = root.GetProperty("operations").EnumerateArray().ToList();
        var summary = root.GetProperty("summary");
        summary.GetProperty("total").GetInt32().Should().Be(operations.Count);
        var byStatus = summary.GetProperty("byStatus");
        foreach (var status in new[] { "proven", "partially-proven", "unproven" })
        {
            byStatus.GetProperty(status).GetInt32().Should().Be(
                operations.Count(row => RequiredString(row, "status") == status));
        }

        var byEntryPoint = summary.GetProperty("byEntryPoint");
        foreach (var entryPoint in new[] { "job", "protocol", "workflow" })
        {
            byEntryPoint.GetProperty(entryPoint).GetInt32().Should().Be(
                operations.Count(row => RequiredString(row, "entryPoint") == entryPoint));
        }

        // honua-server#4408: the exact-ten-element literal range this replaced could
        // never shrink even after #3848/#3850/#3851 closed — removing a resolved gap
        // from the manifest failed the test instead of passing it. The manifest now
        // owns membership; the guard only enforces the reference shape, so shared-gap
        // closure is reconciled by editing this list, not by fighting the test.
        var sharedRuntimeGaps = root.GetProperty("sharedRuntimeGaps")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        sharedRuntimeGaps.Should().NotBeEmpty(
            "the GP runtime carries at least one shared execution-substrate gap until #3849/#3852-#3857 close");
        sharedRuntimeGaps.Should().OnlyHaveUniqueItems(
            "a shared runtime gap must not be listed twice");
        sharedRuntimeGaps.Should().AllSatisfy(
            issue => issue.Should().MatchRegex(
                @"^https://github\.com/honua-io/honua-server/issues/\d+$",
                "each shared runtime gap must be a concrete honua-server issue reference"));

        // honua-server#4408: recorded but never re-checked, `audit.catalogSourceRevision`
        // let an executor change silently outlive the audit that proved it. This digest
        // is recomputed from the tree every run and fails loudly instead.
        var audit = root.GetProperty("audit");
        RequiredString(audit, "catalogSourceContentDigest").Should().Be(
            ComputeCatalogSourceContentDigest(ArchitectureTestHelpers.ResolveRepositoryRoot()),
            "a change under BuiltInProcessCatalog.cs, ProcessExecutionCapabilityCatalog.cs, "
            + "ProcessDestructiveClassifier.cs, GeoprocessingServiceCollectionExtensions.cs or "
            + "Features/Geoprocessing/Execution/ can invalidate any 'proven' verdict computed against "
            + "the prior source; re-audit the affected rows and record the new digest in "
            + "audit.catalogSourceContentDigest");
    }

    /// <summary>
    /// Finds the xunit test method named <paramref name="testName"/> in
    /// <paramref name="evidencePath"/> and returns the source text the proof actually
    /// runs: the method body, or — for a thin <c>=&gt; Helper(args)</c> forwarder, the
    /// common shape for parameterized proof pairs — that expression plus the body of
    /// the helper it calls (chased up to 4 levels). Returns <see langword="null"/> when
    /// no method with that name exists.
    /// </summary>
    internal static string? ResolveTestMethodBody(string evidencePath, string testName)
    {
        var text = File.ReadAllText(evidencePath);
        var root = CSharpSyntaxTree.ParseText(text, path: evidencePath).GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == testName);
        return method is null ? null : ResolveMethodText(method, new HashSet<string>(StringComparer.Ordinal), depth: 0);
    }

    private static string ResolveMethodText(MethodDeclarationSyntax method, HashSet<string> seen, int depth)
    {
        if (method.Body is not null)
        {
            return method.Body.ToString();
        }

        var expression = method.ExpressionBody?.Expression;
        if (expression is null)
        {
            return string.Empty;
        }

        var text = expression.ToString();
        if (depth >= 4)
        {
            return text;
        }

        var invocation = expression switch
        {
            InvocationExpressionSyntax direct => direct,
            AwaitExpressionSyntax { Expression: InvocationExpressionSyntax awaited } => awaited,
            _ => null,
        };

        if (invocation?.Expression is IdentifierNameSyntax callee && seen.Add(callee.Identifier.Text))
        {
            var calleeMethod = method.FirstAncestorOrSelf<TypeDeclarationSyntax>()?
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == callee.Identifier.Text);
            if (calleeMethod is not null)
            {
                return text + "\n" + ResolveMethodText(calleeMethod, seen, depth + 1);
            }
        }

        return text;
    }

    /// <summary>
    /// honua.gp-catalog-source-digest/v1: sha256sum-shaped lines for every <c>.cs</c> file
    /// under <see cref="CatalogSourceRoots"/>, ordered by repo-relative path, hashed once more.
    /// </summary>
    internal static string ComputeCatalogSourceContentDigest(string repositoryRoot)
    {
        var files = new List<string>();
        foreach (var root in CatalogSourceRoots)
        {
            var rootPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, root.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(rootPath))
            {
                files.AddRange(Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories));
            }
            else
            {
                File.Exists(rootPath).Should().BeTrue($"catalog source root should exist: {root}");
                files.Add(rootPath);
            }
        }

        var lines = files
            .Select(path => (
                RelativePath: Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                Digest: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => $"{entry.Digest}  {entry.RelativePath}\n");

        return ComputeDigest(string.Concat(lines));
    }

    internal static string ComputeDigest(string content)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static JsonDocument ReadManifest()
    {
        var manifestPath = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(manifestPath).Should().BeTrue(
            $"the whole-catalog GP execution matrix should exist at {ManifestRelativePath}");

        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        element.TryGetProperty(propertyName, out var property).Should().BeTrue(
            $"matrix property '{propertyName}' is required");
        var value = property.GetString();
        value.Should().NotBeNullOrWhiteSpace($"matrix property '{propertyName}' is required");
        return value!;
    }
}
