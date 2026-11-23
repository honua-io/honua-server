// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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
        var marker = UnconsumedMarker();
        var gate = Gate(QualificationFault.Parse("Wedged:" + Token), marker);

        (await gate.GetActiveAsync()).Should().Be(QualificationFaultKind.Wedged);
        (await gate.GetActiveAsync()).Should().Be(QualificationFaultKind.Wedged);
        await marker.Received(1).ShouldWedgeAsync(Token, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task GetActiveAsync_WedgeConsumedByEarlierRollout_ServesNormally()
        => (await Gate(QualificationFault.Parse("Wedged:" + Token), ConsumedMarker()).GetActiveAsync())
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
    public async Task GetActiveAsync_CancelledRequestDoesNotCancelTheSharedDecision()
    {
        var resolution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync(Token, Arg.Any<CancellationToken>()).Returns(resolution.Task);
        var gate = Gate(QualificationFault.Parse("Wedged:" + Token), marker);
        using var request = new CancellationTokenSource();

        var cancelled = gate.GetActiveAsync(request.Token);
        request.Cancel();

        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>();
        resolution.SetResult(true);
        (await gate.GetActiveAsync()).Should().Be(QualificationFaultKind.Wedged);
    }

    [UnitTest]
    public async Task GetActiveAsync_StoppingHostCancelsTheSharedDecision()
    {
        using var stopping = new CancellationTokenSource();
        var marker = Substitute.For<IQualificationWedgeMarker>();
        CancellationToken markerToken = default;
        marker.ShouldWedgeAsync(Token, Arg.Any<CancellationToken>()).Returns(call =>
        {
            markerToken = call.Arg<CancellationToken>();
            return new TaskCompletionSource<bool>().Task;
        });
        var gate = new QualificationFaultGate(
            new QualificationWedgeDecision(QualificationFault.Parse("Wedged:" + Token), marker, stopping.Token),
            NullLogger<QualificationFaultGate>.Instance);

        var decision = gate.GetActiveAsync();
        stopping.Cancel();

        markerToken.IsCancellationRequested.Should().BeTrue();
        (await decision.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(QualificationFaultKind.None);
    }

    [UnitTest]
    public async Task GetActiveAsync_StalledMarkerFallsBackToServing()
    {
        var resolution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync(Token, Arg.Any<CancellationToken>()).Returns(resolution.Task);
        var gate = Gate(QualificationFault.Parse("Wedged:" + Token), marker);

        (await gate.GetActiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(QualificationFaultKind.None);
    }

    [UnitTest]
    public async Task GetActiveAsync_ServeErrors_NeedsNoMarker()
        => (await Gate(QualificationFault.Parse("ServeErrors"), marker: null).GetActiveAsync())
            .Should().Be(QualificationFaultKind.ServeErrors);

    [UnitTest]
    public async Task AddQualificationFault_ServeErrorsInConfiguration_ActivatesWithoutTheSharedDatabase()
    {
        using var provider = Register("ServeErrors").BuildServiceProvider();

        var gate = provider.GetRequiredService<QualificationFaultGate>();

        gate.IsConfigured.Should().BeTrue();
        (await gate.GetActiveAsync()).Should().Be(QualificationFaultKind.ServeErrors);
    }

    [UnitTest]
    public async Task AddQualificationFault_WedgedWithoutSharedDatabase_ServesNormally()
    {
        var services = Register("Wedged:" + Token);
        services.AddSingleton(provider => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>()));
        using var provider = services.BuildServiceProvider();

        (await provider.GetRequiredService<QualificationFaultGate>().GetActiveAsync()).Should().Be(QualificationFaultKind.None);
    }

    [UnitTest]
    public void AddQualificationFault_SettingAbsent_IsInert()
    {
        using var provider = Register(value: null).BuildServiceProvider();

        provider.GetRequiredService<QualificationFaultGate>().IsConfigured.Should().BeFalse();
    }

    [UnitTest]
    public void AddQualificationFault_InvalidValue_FailsStartup()
    {
        using var provider = Register("Wedged:short").BuildServiceProvider();
        var start = () => provider.GetRequiredService<QualificationFaultGate>();

        start.Should().Throw<InvalidOperationException>().WithMessage($"*{QualificationFault.ConfigurationKey}*");
    }

    private static ServiceCollection Register(string? value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [QualificationFault.ConfigurationKey] = value })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddQualificationFault();
        return services;
    }

    internal static QualificationFaultGate Gate(QualificationFault fault, IQualificationWedgeMarker? marker)
        => new(new QualificationWedgeDecision(fault, marker), NullLogger<QualificationFaultGate>.Instance);

    internal static IQualificationWedgeMarker UnconsumedMarker()
    {
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return marker;
    }

    internal static IQualificationWedgeMarker ConsumedMarker()
    {
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        return marker;
    }
}
