// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Tests.Features.ControlPlane;

/// <summary>
/// Fence coverage for the Demo B smoke-gate fault injection. The injection must be inert by default
/// and must never fire against an environment that is not on the explicit allow-list, so a
/// misconfiguration can never fail a real (e.g. production / live) metadata release.
/// </summary>
public sealed class MetadataReleaseFaultInjectionOptionsTests
{
    [Fact]
    public void ShouldFailSmoke_DefaultOptions_IsInert()
    {
        var options = new MetadataReleaseFaultInjectionOptions();

        options.Enabled.Should().BeFalse();
        options.ForceSmokeFailure.Should().BeFalse();
        options.ShouldFailSmoke("staging").Should().BeFalse();
        options.ShouldFailSmoke("production").Should().BeFalse();
    }

    [Fact]
    public void ShouldFailSmoke_EnabledButNotForced_DoesNotInject()
    {
        var options = new MetadataReleaseFaultInjectionOptions { Enabled = true, ForceSmokeFailure = false };

        options.ShouldFailSmoke("staging").Should().BeFalse();
    }

    [Fact]
    public void ShouldFailSmoke_EnabledAndForced_InjectsForAllowedEnvironmentOnly()
    {
        var options = new MetadataReleaseFaultInjectionOptions
        {
            Enabled = true,
            ForceSmokeFailure = true,
            AllowedEnvironments = new[] { "staging", "dev" }
        };

        options.ShouldFailSmoke("staging").Should().BeTrue();
        options.ShouldFailSmoke("STAGING").Should().BeTrue("the allow-list match is case-insensitive");
        options.ShouldFailSmoke("dev").Should().BeTrue();

        // Hard fence: any environment not on the allow-list is refused even with injection enabled.
        options.ShouldFailSmoke("production").Should().BeFalse();
        options.ShouldFailSmoke("live").Should().BeFalse();
        options.ShouldFailSmoke(null).Should().BeFalse();
        options.ShouldFailSmoke("  ").Should().BeFalse();
    }

    [Fact]
    public void ShouldFailSmoke_EmptyAllowList_NeverInjects()
    {
        var options = new MetadataReleaseFaultInjectionOptions
        {
            Enabled = true,
            ForceSmokeFailure = true,
            AllowedEnvironments = Array.Empty<string>()
        };

        options.ShouldFailSmoke("staging").Should().BeFalse();
    }
}
