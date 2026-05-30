// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ratchet that asserts no source file under the audit-A1 OData-required
/// Infrastructure sub-areas takes a using-clause dependency on any
/// <c>Honua.Server.Features.Protocols.*</c> namespace. Replacing those
/// back-edges with delegate-override registrations (see
/// <see cref="Honua.Infrastructure.Models.StandardErrorResponseFormatter"/>
/// and
/// <see cref="Honua.Infrastructure.Validation.LayerValidationHelpers"/>
/// for the canonical pattern) is what unblocks Phase 1 protocol-assembly
/// extraction. This ratchet locks in the cleanup so a future PR cannot
/// silently regress it.
/// </summary>
/// <remarks>
/// Detection is source-based (scanning the verbatim using directives in
/// each <c>.cs</c> file) rather than reflection-based, because some
/// back-edges manifest only via fully-qualified type references that a
/// reflection walk over method signatures cannot observe.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class InfrastructureProtocolBackEdgeRatchetTests
{
    private static readonly string[] ODataRequiredSubAreas =
    {
        "Authentication",
        "Caching",
        "Events",
        "Helpers",
        "Models",
        "Validation",
    };

    private const string ProtocolsNamespacePrefix = "using Honua.Server.Features.Protocols";

    [ArchitectureTest]
    public void ODataRequiredInfrastructureSubAreas_ShouldNotUsing_AnyProtocolNamespace()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var infrastructureRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Honua.Server",
            "Features",
            "Infrastructure");

        Directory
            .Exists(infrastructureRoot)
            .Should()
            .BeTrue("the Infrastructure root must exist for the ratchet to scan");

        var violations = new List<string>();

        foreach (var subArea in ODataRequiredSubAreas)
        {
            var subAreaDir = Path.Combine(infrastructureRoot, subArea);
            if (!Directory.Exists(subAreaDir))
            {
                // The sub-area may have been extracted to a new assembly /
                // location by a follow-up PR; that case is checked by a
                // sibling test, not this one. Skip silently.
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(subAreaDir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.StartsWith(ProtocolsNamespacePrefix, StringComparison.Ordinal))
                    {
                        violations.Add($"{Path.GetRelativePath(repositoryRoot, file)}: {line.Trim()}");
                        break;
                    }
                }
            }
        }

        violations
            .Should()
            .BeEmpty(
                "no file under the audit-A1 OData-required Infrastructure sub-areas may take a "
                + "using-clause dependency on a Honua.Server.Features.Protocols.* namespace. "
                + "Use the delegate-override pattern (see StandardErrorResponseFormatter or "
                + "LayerValidationHelpers) so the protocol assembly registers behaviour at "
                + "startup instead.");
    }
}
