// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server;
using NetArchTest.Rules;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ratchet that locks in the audit-A1 misfile relocations
/// (memory: <c>structural-audit-2026-05</c>, A1). When a sub-area moves out
/// of <c>Honua.Server.Features.Infrastructure</c> to its proper feature
/// directory, this test asserts the old namespace stays empty so a
/// re-introduced misfile fails the gate at PR time instead of silently
/// growing the grab-bag again.
/// </summary>
/// <remarks>
/// Add an assertion here every time an Infrastructure sub-area is
/// unmisfiled. Do not remove entries — the audit explicitly noted the
/// grab-bag grew unchecked because the Infrastructure exemption let
/// drift in (<c>VerticalSliceIsolationTests.cs:44,88</c>). This ratchet
/// is the replacement guardrail for the relocated sub-areas.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class InfrastructureMisfileRatchetTests
{
    private static readonly string[] RelocatedInfrastructureSubAreas =
    {
        // Relocated commits (audit-A1):
        // - 22b4e6df3 refactor(infra): unmisfile ControlPlane out of Infrastructure
        // - 5a1c3412d refactor(infra): unmisfile Styling out of Infrastructure
        "Honua.Server.Features.Infrastructure.ControlPlane",
        "Honua.Server.Features.Infrastructure.Styling",
    };

    [ArchitectureTest]
    public void RelocatedSubAreas_ShouldNotReappearUnder_Infrastructure()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;

        var allTypes = ArchitectureTestHelpers.GetTypesSafely(serverAssembly).ToList();

        foreach (var bannedNamespace in RelocatedInfrastructureSubAreas)
        {
            var resurrected = allTypes
                .Where(t => t.Namespace is not null)
                .Where(t => t.Namespace == bannedNamespace
                            || t.Namespace!.StartsWith(bannedNamespace + ".", StringComparison.Ordinal))
                .Select(t => t.FullName)
                .ToList();

            resurrected
                .Should()
                .BeEmpty(
                    "the {0} sub-area was relocated out of the Infrastructure grab-bag per audit-A1; "
                    + "moving types back into the old namespace would re-introduce the misfile",
                    bannedNamespace);
        }
    }

    [ArchitectureTest]
    public void RelocatedSubAreas_ShouldExistAt_NewLocation()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;

        var allTypes = ArchitectureTestHelpers.GetTypesSafely(serverAssembly).ToList();

        var relocatedTargets = new[]
        {
            "Honua.Server.Features.ControlPlane",
            "Honua.Server.Features.Styling",
        };

        foreach (var target in relocatedTargets)
        {
            var presentAtTarget = allTypes
                .Where(t => t.Namespace is not null)
                .Any(t => t.Namespace == target
                          || t.Namespace!.StartsWith(target + ".", StringComparison.Ordinal));

            presentAtTarget
                .Should()
                .BeTrue("the relocated sub-area {0} must exist at its new top-level Feature location", target);
        }
    }
}
