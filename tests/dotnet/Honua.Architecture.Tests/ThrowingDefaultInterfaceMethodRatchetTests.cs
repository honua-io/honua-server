// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Audit-C1 ratchet: ensures the Metadata v2 interfaces in
/// <c>Honua.Core/Features/{Validation,Query,Edit}/</c> do not regrow throwing
/// default-interface-methods. A v2 interface method that still ships as a
/// <c>throw new NotSupportedException(...)</c> default body silently fails at
/// runtime when an implementation forgets to override it. Every such stub must
/// either be promoted to <c>abstract</c> (no default body) — making the compiler
/// force every implementer to ship a working override — or carry an explicit
/// <c>Audit-C1</c> XML-doc marker that admits the deferred work.
/// </summary>
/// <remarks>
/// Mirrors the source-scan pattern of the existing arch tests: read the
/// interface source files directly so the assertion runs against the canonical
/// shape, not the reflective surface (an <c>abstract</c> method and a default
/// throwing body both look the same to reflection).
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class ThrowingDefaultInterfaceMethodRatchetTests
{
    /// <summary>
    /// V2 interface files audit-C1 covers. Each path is rooted at the repository root.
    /// </summary>
    private static readonly string[] _v2InterfaceFiles =
    {
        "src/Honua.Core/Features/Validation/Abstractions/IResourceValidator.cs",
        "src/Honua.Core/Features/Query/IQueryProcessor.cs",
        "src/Honua.Core/Features/Query/IQueryParameterAdapter.cs",
        "src/Honua.Core/Features/Edit/IEditParameterAdapter.cs",
    };

    [ArchitectureTest]
    public void V2_InterfaceMethods_ShouldNotShipThrowingDefaults_WithoutMarker()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var violations = new List<string>();
        foreach (var relativePath in _v2InterfaceFiles)
        {
            var absolutePath = Path.Combine(repositoryRoot, relativePath);
            File.Exists(absolutePath).Should().BeTrue(
                $"audit-C1 v2 interface file '{relativePath}' must exist; update the ratchet list if it was renamed.");

            var source = File.ReadAllText(absolutePath);

            // Locate every "=> throw new NotSupportedException(...)" or "throw new NotSupportedException(...)"
            // default body and demand that an Audit-C1 XML marker live in the same source file. The
            // simplest robust signal is "Audit-C1" appearing in the file; the marker is mandatory on
            // any deferred stub, so its absence means the file regrew an unmarked throwing default.
            var throwMatches = Regex.Matches(
                source,
                @"throw\s+new\s+NotSupportedException\b",
                RegexOptions.None,
                TimeSpan.FromSeconds(1));

            if (throwMatches.Count == 0)
            {
                continue;
            }

            if (!source.Contains("Audit-C1", StringComparison.Ordinal))
            {
                violations.Add(
                    $"'{relativePath}' contains {throwMatches.Count} 'throw new NotSupportedException' " +
                    "occurrence(s) but no 'Audit-C1' XML-doc marker. Either (a) promote the default-interface " +
                    "method to abstract (remove the default body) so the compiler forces every implementer " +
                    "to ship a working override, or (b) add a <remarks>Audit-C1: ...</remarks> XML doc to " +
                    "the deferred stub explaining the gap and the tracking issue.");
            }
        }

        violations.Should().BeEmpty(
            "Audit-C1 prohibits throwing default-interface-methods on Metadata v2 contracts without an " +
            "Audit-C1 XML marker. See structural-audit-2026-05 (group C1) for rationale.");
    }
}
