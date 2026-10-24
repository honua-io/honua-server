// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class HarnessLifetimeTests
{
    [Fact]
    public void TryCancel_OnDisposedSource_DoesNotThrow()
    {
        var source = new CancellationTokenSource();
        source.Dispose();

        var act = () => HarnessLifetime.TryCancel(source);

        act.Should().NotThrow<ObjectDisposedException>();
        act().Should().BeFalse();
    }

    [Fact]
    public void TryCancel_CancelsOnce_ThenReportsFalse()
    {
        using var source = new CancellationTokenSource();

        HarnessLifetime.TryCancel(source).Should().BeTrue();
        source.IsCancellationRequested.Should().BeTrue();
        HarnessLifetime.TryCancel(source).Should().BeFalse();
    }

    [Fact]
    public void Register_ThenDisposeInUsingOrder_LeavesNoExitHandlerOnADisposedSource()
    {
        // Mirrors Program.cs: the registration is declared after the source, so `using`
        // disposes it first; a later process-exit event then has nothing to cancel.
        var source = new CancellationTokenSource();
        var registration = HarnessLifetime.Register(source);

        registration.Dispose();
        registration.Dispose();
        source.Dispose();

        var act = () => HarnessLifetime.TryCancel(source);
        act.Should().NotThrow();
    }
}
