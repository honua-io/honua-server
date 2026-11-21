// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Hosting;

// Only the operator-error refusals become a plain refusal; any other startup failure still surfaces.
public sealed class StartupRefusalPassesTests
{
    [UnitTest]
    public void Passes_CheckThatSucceeds_ReturnsTrue()
        => StartupRefusal.Passes(static () => { }).Should().BeTrue();

    [UnitTest]
    public void Passes_DurableJobStoreRefusal_ReportsARefusal()
    {
        var previous = Environment.ExitCode;
        try
        {
            StartupRefusal.Passes(static () => throw new DurableJobSubstrateUnavailableException()).Should().BeFalse();
            Environment.ExitCode.Should().Be(StartupRefusal.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [UnitTest]
    public void Passes_UnrelatedFailure_IsNotSwallowed()
    {
        var check = static () => StartupRefusal.Passes(static () => throw new InvalidOperationException("composition bug"));
        check.Should().Throw<InvalidOperationException>().WithMessage("composition bug");
    }
}
