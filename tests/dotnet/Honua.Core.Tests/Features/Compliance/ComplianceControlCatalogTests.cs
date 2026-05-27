// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;
using Honua.Core.Features.Compliance.Services;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for <see cref="DefaultComplianceControlCatalog"/>. Locks in the
/// curated control set so a future change here is intentional, not accidental.
/// </summary>
public sealed class ComplianceControlCatalogTests
{
    [Fact]
    public void Catalog_IncludesSoc2AndFedRampControls()
    {
        var catalog = new DefaultComplianceControlCatalog();
        var byFramework = catalog.Controls.GroupBy(c => c.Framework).ToDictionary(g => g.Key, g => g.Count());

        byFramework.Should().ContainKey(ComplianceFramework.Soc2);
        byFramework.Should().ContainKey(ComplianceFramework.FedRamp);
        byFramework[ComplianceFramework.Soc2].Should().BeGreaterThanOrEqualTo(3);
        byFramework[ComplianceFramework.FedRamp].Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Catalog_AllControlsHaveStableId()
    {
        var catalog = new DefaultComplianceControlCatalog();

        catalog.Controls.Select(c => c.ControlId).Should().OnlyHaveUniqueItems();
        catalog.Controls.Should().AllSatisfy(c =>
        {
            c.ControlId.Should().NotBeNullOrWhiteSpace();
            c.Title.Should().NotBeNullOrWhiteSpace();
            c.Description.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public void GetControl_ReturnsControlOrNull()
    {
        var catalog = new DefaultComplianceControlCatalog();

        catalog.GetControl("soc2.cc6.1").Should().NotBeNull();
        catalog.GetControl("SOC2.CC6.1").Should().NotBeNull("lookup is case-insensitive");
        catalog.GetControl("missing").Should().BeNull();
        catalog.GetControl(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Catalog_FedRampControlsCoverBoundaryAndEncryption()
    {
        var catalog = new DefaultComplianceControlCatalog();
        var ids = catalog.Controls
            .Where(c => c.Framework == ComplianceFramework.FedRamp)
            .Select(c => c.ControlId)
            .ToList();

        ids.Should().Contain("fedramp.sc-7", "FedRAMP boundary protection control must be tracked");
        ids.Should().Contain("fedramp.sc-13", "FedRAMP cryptographic protection control must be tracked");
        ids.Should().Contain("fedramp.sc-28", "FedRAMP at-rest cryptographic protection must be tracked");
    }

    [Fact]
    public void Catalog_DependencyDeclarationsAreNonEmpty()
    {
        var catalog = new DefaultComplianceControlCatalog();

        var withDependencies = catalog.Controls.Count(c => c.Dependencies.Count > 0);
        withDependencies.Should().Be(catalog.Controls.Count,
            "every control should declare its platform dependencies so the gate can substantiate or block readiness");
    }
}
