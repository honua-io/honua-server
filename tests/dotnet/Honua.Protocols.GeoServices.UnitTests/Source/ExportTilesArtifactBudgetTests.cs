// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

[Protocol(TestProtocols.MapServer)]
public sealed class ExportTilesArtifactBudgetTests
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static readonly ClaimsPrincipal SignedIn = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "analyst")], "Bearer"));

    [UnitTest]
    [Operation(Operations.Export)]
    public void AnonymousExports_StopAtTheHourlyBudgetAndResumeInTheNextWindow()
    {
        var clock = new SteppedClock();
        var budget = Budget(clock, perHour: 2);

        budget.TryTake(Anonymous).Should().BeNull();
        budget.TryTake(Anonymous).Should().BeNull();
        budget.TryTake(Anonymous).Should().Be(3600, "the third anonymous archive waits for the next window");

        clock.Now += TimeSpan.FromHours(1);
        budget.TryTake(Anonymous).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void SignedInExports_DoNotUseTheAnonymousBudget()
    {
        var budget = Budget(new SteppedClock(), perHour: 0);

        budget.TryTake(SignedIn).Should().BeNull();
        budget.TryTake(Anonymous).Should().NotBeNull("a zero budget refuses anonymous archives");
    }

    private static ExportTilesArtifactBudget Budget(TimeProvider clock, int perHour)
        => new(Options.Create(new ExportTilesArtifactOptions { AnonymousArtifactsPerHour = perHour }), clock);

    private sealed class SteppedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
