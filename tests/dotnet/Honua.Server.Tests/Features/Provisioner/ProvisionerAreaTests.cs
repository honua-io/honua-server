// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Provisioner.BuildJobs;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Provisioner;

/// <summary>
/// Unit coverage for the per-area build-job area selector. The same AREA forms the
/// open-data provisioner accepts (<c>bbox:</c> / <c>geoid:</c>) must parse and validate
/// here so a Maui geocoder/router build can be driven by, e.g., <c>geoid:15009</c>
/// (Maui County) or a Maui bbox, and malformed input fails cleanly with a caller-facing
/// reason rather than throwing.
/// </summary>
public sealed class ProvisionerAreaTests
{
    [UnitTest]
    public void TryParse_MauiCountyGeoid_ParsesStateAndCounty()
    {
        ProvisionerArea.TryParse("geoid:15009", out var area, out var error).Should().BeTrue();
        error.Should().BeEmpty();
        area.Kind.Should().Be(ProvisionerAreaKind.CountyGeoid);
        area.CountyGeoid.Should().Be("15009");
        area.StateFips.Should().Be("15");
        area.ToParameterValue().Should().Be("geoid:15009");
    }

    [UnitTest]
    public void TryParse_StateFips_ParsesState()
    {
        ProvisionerArea.TryParse("geoid:15", out var area, out var error).Should().BeTrue();
        error.Should().BeEmpty();
        area.Kind.Should().Be(ProvisionerAreaKind.StateFips);
        area.StateFips.Should().Be("15");
        area.CountyGeoid.Should().BeNull();
    }

    [UnitTest]
    public void TryParse_MauiBbox_ParsesEnvelope()
    {
        ProvisionerArea.TryParse("bbox:-156.70,20.57,-155.98,21.03", out var area, out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
        area.Kind.Should().Be(ProvisionerAreaKind.Bbox);
        area.Bbox.Should().Equal(-156.70, 20.57, -155.98, 21.03);
    }

    [UnitTest]
    public void TryParse_NullOrEmpty_FailsCleanly()
    {
        ProvisionerArea.TryParse(null, out _, out var error).Should().BeFalse();
        error.Should().NotBeEmpty();
        ProvisionerArea.TryParse("   ", out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryParse_UnknownPrefix_Fails()
    {
        ProvisionerArea.TryParse("county:Maui", out _, out var error).Should().BeFalse();
        error.Should().Contain("bbox");
    }

    [UnitTest]
    public void TryParse_BboxWrongArity_Fails()
    {
        ProvisionerArea.TryParse("bbox:-156.7,20.57,-155.98", out _, out var error).Should().BeFalse();
        error.Should().Contain("four");
    }

    [UnitTest]
    public void TryParse_BboxInverted_Fails()
    {
        // maxLon < minLon
        ProvisionerArea.TryParse("bbox:-155.98,20.57,-156.70,21.03", out _, out var error).Should().BeFalse();
        error.Should().Contain("minLon < maxLon");
    }

    [UnitTest]
    public void TryParse_BboxOutOfRange_Fails()
    {
        ProvisionerArea.TryParse("bbox:-200,20,10,30", out _, out var error).Should().BeFalse();
        error.Should().Contain("range");
    }

    [UnitTest]
    public void TryParse_NonNumericGeoid_Fails()
    {
        ProvisionerArea.TryParse("geoid:MAUI", out _, out var error).Should().BeFalse();
        error.Should().Contain("numeric");
    }

    [UnitTest]
    public void TryParse_WrongLengthGeoid_Fails()
    {
        ProvisionerArea.TryParse("geoid:150", out _, out var error).Should().BeFalse();
        error.Should().Contain("2 digits");
    }
}
