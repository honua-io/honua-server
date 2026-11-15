// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// The geometry bytes one GeometryServer request may build. In process; the component host is
// not needed.
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryServiceResultBudgetTests
{
    private const GeometryServiceCaller SignedIn = GeometryServiceCaller.SignedIn;
    private const GeometryServiceCaller NotSignedIn = GeometryServiceCaller.NotSignedIn;

    [UnitTest]
    [Operation(Operations.Buffer)]
    public void SmallRequest_GetsTheCallersAllowance()
    {
        var largest = new LimitsOptions().Edits.MaxPayloadSize;

        Budget(SignedIn, "{}").Bytes.Should().Be(largest);
        Budget(NotSignedIn, "{}").Bytes.Should().Be(largest / GeometryServiceResultBudget.NotSignedInAllowanceDivisor);
    }

    [UnitTest]
    [Operation(Operations.Buffer)]
    public void LargeRequest_MayBuildTwiceWhatItSent()
    {
        var limits = new LimitsOptions();
        limits.Edits.MaxPayloadSize = 1_000;
        var context = GeometryServiceTestContexts.Create(NotSignedIn, limits);

        GeometryServiceResultBudget.Create(context, [new string('g', 600)], [new string('g', 400)])
            .Bytes.Should().Be(2_000);
    }

    [UnitTest]
    [Operation(Operations.Buffer)]
    public void Budget_FollowsTheConfiguredPayloadLimit()
    {
        var limits = new LimitsOptions();
        limits.Edits.MaxPayloadSize = 4_000;

        GeometryServiceResultBudget.Create(GeometryServiceTestContexts.Create(NotSignedIn, limits), ["{}"])
            .Bytes.Should().Be(1_000);
    }

    [UnitTest]
    [Operation(Operations.Buffer)]
    public void Results_AreCountedUntilTheBudgetIsPassed()
    {
        var budget = new GeometryServiceResultBudget(100);

        budget.Add(new byte[60]).Should().HaveCount(60);
        budget.Add(new byte[40]).Should().HaveCount(40);

        var act = () => budget.Add(new byte[1]);

        act.Should().Throw<GeometryServiceLimitException>()
            .WithMessage(GeometryServiceResultBudget.RefusalMessage)
            .Which.Should().BeAssignableTo<ArgumentException>("handlers answer it as a 400");
    }

    [UnitTest]
    [Operation(Operations.Buffer)]
    public void Estimate_ChecksWhatIsLeftWithoutCountingIt()
    {
        var budget = new GeometryServiceResultBudget(100);
        budget.Add(new byte[30]);

        budget.Fits(70).Should().BeTrue();
        budget.Fits(71).Should().BeFalse();
        budget.Fits(-1).Should().BeFalse();
        budget.Add(new byte[70]).Should().HaveCount(70);
    }

    private static GeometryServiceResultBudget Budget(GeometryServiceCaller caller, string geometry)
        => GeometryServiceResultBudget.Create(GeometryServiceTestContexts.Create(caller), [geometry]);
}
