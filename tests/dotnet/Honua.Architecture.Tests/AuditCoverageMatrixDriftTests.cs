// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards against silent drift between <see cref="DefaultAuditActionResolver"/>
/// and <c>docs/internal/operator/audit-coverage-matrix.md</c>. The resolver is
/// the code form for auth route handling, so these row-level claims need a hard
/// join check.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class AuditCoverageMatrixDriftTests
{
    private const string SourceRelativePath = "src/Honua.Hosting/Features/Middleware/DefaultAuditActionResolver.cs";
    private const string DocRelativePath = "docs/internal/operator/audit-coverage-matrix.md";
    private const string AuthSectionHeader = "### Authentication and authorization";

    [Fact]
    public void ResolverDocReference_IsExpectedRelativePath()
    {
        var source = ReadSource();
        source.Should().Contain(
            "docs/internal/operator/audit-coverage-matrix.md",
            "the resolver doc-comment must point at the canonical audit matrix in docs/internal");
    }

    [Fact]
    public void ResolverNamedAuthRoutes_AreDocumentedInAuditCoverageMatrix()
    {
        var source = ReadSource();
        var docText = ReadDoc();

        var resolverRoutes = ParseResolverNamedRoutes(source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        resolverRoutes.Should().NotBeEmpty("resolver should still expose explicit auth routes");

        var docRoutes = ParseAuthRoutesFromDoc(docText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        resolverRoutes.Should().BeSubsetOf(
            docRoutes,
            "every explicit resolver auth route/method should be documented in the matrix");
    }

    [Fact]
    public void AuditCoverageMatrixAuthRoutes_HaveResolverCoverage()
    {
        var source = ReadSource();
        var docText = ReadDoc();

        var resolverRoutes = ParseResolverNamedRoutes(source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var docRoutes = ParseAuthRoutesFromDoc(docText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        docRoutes.Should().BeSubsetOf(
            resolverRoutes,
            "the matrix should not document an explicit auth route that the resolver does not classify");
    }

    private static string ReadSource()
    {
        var sourcePath = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(sourcePath).Should().BeTrue($"resolver source should exist at {SourceRelativePath}");
        return File.ReadAllText(sourcePath);
    }

    private static string ReadDoc()
    {
        var docPath = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            DocRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(docPath).Should().BeTrue($"the audit coverage matrix should exist at {DocRelativePath}");
        return File.ReadAllText(docPath);
    }

    private static IEnumerable<string> ParseResolverNamedRoutes(string source)
    {
        // The explicit auth routes are declared via Add("METHOD", "/route", descriptor).
        var regex = new Regex(
            @"Add\(""(?<method>[A-Za-z]+)""\s*,\s*""(?<route>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.Singleline);
        foreach (Match match in regex.Matches(source))
        {
            var method = match.Groups["method"].Value.Trim();
            var route = NormalizeRoute(match.Groups["route"].Value.Trim());
            if (!string.IsNullOrWhiteSpace(method) && !string.IsNullOrWhiteSpace(route))
            {
                yield return $"{method} {route}";
            }
        }
    }

    private static IEnumerable<string> ParseAuthRoutesFromDoc(string docText)
    {
        var authSectionStart = docText.IndexOf(AuthSectionHeader, StringComparison.Ordinal);
        authSectionStart.Should().BeGreaterThanOrEqualTo(0, "audit matrix must contain the auth section");

        var sectionText = docText[authSectionStart..];
        var nextSection = sectionText.IndexOf("\n### ", StringComparison.Ordinal);
        if (nextSection >= 0)
        {
            nextSection = nextSection > 0 ? nextSection : -1;
            if (nextSection > 0)
            {
                sectionText = sectionText[..nextSection];
            }
        }

        var tableRows = sectionText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith('|'))
            .ToList();

        foreach (var row in tableRows)
        {
            var line = row.Trim();
            if (line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Trim('|')
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .ToArray();
            if (cells.Length < 6)
            {
                continue;
            }

            var routeCell = cells[1];
            var methodCell = cells[2];
            if (routeCell.Contains("**") || methodCell.Contains("any", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var routes = Regex.Matches(routeCell, @"`([^`]+)`")
                .Select(routeMatch => NormalizeRoute(routeMatch.Groups[1].Value))
                .Where(route => !string.IsNullOrWhiteSpace(route))
                .Select(route => route);

            var methods = methodCell
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(method => method.Trim().Trim('`'))
                .Where(method => method.Length > 0 && !method.Contains("**", StringComparison.Ordinal));

            foreach (var route in routes)
            {
                foreach (var method in methods)
                {
                    yield return $"{method} {route}";
                }
            }
        }
    }

    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return string.Empty;
        }

        return route
            .Replace("{version:apiVersion}", "{version}", StringComparison.Ordinal)
            .Trim()
            .TrimEnd('/');
    }
}
