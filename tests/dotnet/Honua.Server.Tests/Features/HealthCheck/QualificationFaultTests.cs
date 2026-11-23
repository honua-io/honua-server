// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.HealthCheck;

[Protocol(TestProtocols.Health)]
public sealed class QualificationFaultTests
{
    private const string Token = "gate-run-0001";

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_SettingAbsent_IsInert(string? value)
        => QualificationFault.Parse(value).Kind.Should().Be(QualificationFaultKind.None);

    [UnitTest]
    public void Parse_ServeErrors_ReturnsServeErrorsWithoutToken()
        => QualificationFault.Parse(" ServeErrors ").Should().Be(new QualificationFault(QualificationFaultKind.ServeErrors, null));

    [UnitTest]
    public void Parse_WedgedWithToken_ReturnsWedgedAndToken()
        => QualificationFault.Parse("Wedged:" + Token).Should().Be(new QualificationFault(QualificationFaultKind.Wedged, Token));

    [UnitTheory]
    [InlineData("Wedged")]
    [InlineData("Wedged:")]
    [InlineData("Wedged:short")]
    [InlineData("Wedged:has spaces in it")]
    [InlineData("serveerrors")]
    [InlineData("Crash")]
    public void Parse_UnknownOrMalformedValue_FailsStartupWithTheSettingName(string value)
    {
        var parse = () => QualificationFault.Parse(value);

        parse.Should().Throw<InvalidOperationException>().WithMessage($"*{QualificationFault.ConfigurationKey}*");
    }

    [UnitTest]
    public async Task GetActiveAsync_WedgeNotYetConsumed_IsWedgedAndDecidedOnce()
    {
        var marker = Marker(shouldWedge: true);
        var gate = Gate(QualificationFault.Parse("Wedged:" + Token), marker);

        (await gate.GetActiveAsync()).Should().Be(QualificationFaultKind.Wedged);
        (await gate.GetActiveAsync()).Should().Be(QualificationFaultKind.Wedged);
        await marker.Received(1).ShouldWedgeAsync(Token, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task GetActiveAsync_WedgeConsumedByEarlierRollout_ServesNormally()
        => (await Gate(QualificationFault.Parse("Wedged:" + Token), Marker(shouldWedge: false)).GetActiveAsync())
            .Should().Be(QualificationFaultKind.None);

    [UnitTest]
    public async Task GetActiveAsync_WedgeWithoutSharedDatabase_ServesNormally()
        => (await Gate(QualificationFault.Parse("Wedged:" + Token), marker: null).GetActiveAsync())
            .Should().Be(QualificationFaultKind.None);

    [UnitTest]
    public async Task GetActiveAsync_WedgeMarkerUnreadable_ServesNormally()
    {
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("database unavailable"));

        (await Gate(QualificationFault.Parse("Wedged:" + Token), marker).GetActiveAsync())
            .Should().Be(QualificationFaultKind.None);
    }

    [UnitTest]
    public async Task GetActiveAsync_ServeErrors_NeedsNoMarker()
        => (await Gate(QualificationFault.Parse("ServeErrors"), marker: null).GetActiveAsync())
            .Should().Be(QualificationFaultKind.ServeErrors);

    internal static QualificationFaultGate Gate(QualificationFault fault, IQualificationWedgeMarker? marker)
        => new(fault, marker, NullLogger<QualificationFaultGate>.Instance);

    internal static IQualificationWedgeMarker Marker(bool shouldWedge)
    {
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(shouldWedge);
        return marker;
    }
}
