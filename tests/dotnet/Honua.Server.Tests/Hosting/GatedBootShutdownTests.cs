// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

[Protocol(TestProtocols.Infrastructure)]
public sealed class GatedBootShutdownTests
{
    [UnitTheory]
    [InlineData("Migration safety gate blocked pending contract-phase migration(s) on an existing database", true)]
    [InlineData("Migration safety gate blocked", true)]
    [InlineData("Database migration failed", false)]
    [InlineData("Unrelated failure: Migration safety gate blocked", false)]
    [InlineData("migration safety gate blocked", false)]
    [InlineData("", false)]
    public void IsContractGateRefusal_OnlyRecognizesTerminalGateMessage(string message, bool expected)
    {
        GatedBootShutdown.IsContractGateRefusal(new InvalidOperationException(message))
            .Should().Be(expected);
    }

    [UnitTest]
    public void IsContractGateRefusal_OtherExceptionTypesKeepTheirFailureBehavior()
    {
        GatedBootShutdown.IsContractGateRefusal(new Exception(GatedBootShutdown.ContractGateRefusalPrefix))
            .Should().BeFalse();
        GatedBootShutdown.IsContractGateRefusal(new AggregateException(
            new InvalidOperationException(GatedBootShutdown.ContractGateRefusalPrefix)))
            .Should().BeFalse();
    }
}
