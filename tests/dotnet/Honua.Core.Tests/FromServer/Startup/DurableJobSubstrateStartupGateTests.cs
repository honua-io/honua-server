// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Startup;

public sealed class DurableJobSubstrateStartupGateTests
{
    [UnitTest]
    public void EnsureSatisfied_RequireDurableStoreWithSystemDatabase_IsSatisfied()
    {
        var options = new DurableJobSubstrateOptions { SystemDatabaseConfigured = true };

        var ensure = () => DurableJobSubstrateStartupGate.EnsureSatisfied(options, requireDurableStore: true);

        ensure.Should().NotThrow("the job store is in the system database");
    }

    [UnitTest]
    public void EnsureSatisfied_DurableStoreNotRequiredWithoutSystemDatabase_IsSatisfied()
    {
        var ensure = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            new DurableJobSubstrateOptions(),
            requireDurableStore: false);

        ensure.Should().NotThrow();
    }

    [UnitTest]
    public void EnsureSatisfied_RequireDurableStoreWithoutSystemDatabase_RefusesNamingTheSystemDatabase()
    {
        var ensure = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            new DurableJobSubstrateOptions(),
            requireDurableStore: true);

        var refusal = ensure.Should().Throw<DurableJobSubstrateUnavailableException>().Subject.Single();
        refusal.Cause.Should().Be(DurableJobSubstrateCause.SystemDatabaseNotConfigured);
        refusal.Message.Should().Contain("Jobs:RequireDurableStore")
            .And.Contain("ConnectionStrings:DefaultConnection")
            .And.NotContainEquivalentOf("redis");
        refusal.Remediation.Should().Contain("ConnectionStrings:DefaultConnection")
            .And.NotContainEquivalentOf("redis");
    }

    [UnitTest]
    public void Classify_WithJobStoreAndQueue_IsAvailable()
    {
        new DurableJobSubstrateOptions()
            .Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.Available);
    }

    [UnitTest]
    public void Classify_WithoutSystemDatabase_ReportsTheSystemDatabaseNotConfigured()
    {
        new DurableJobSubstrateOptions()
            .Classify(jobStorePresent: false, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.SystemDatabaseNotConfigured);
    }

    [UnitTest]
    public void Classify_SystemDatabaseWithoutQueue_ReportsAnIncompleteRuntime()
    {
        new DurableJobSubstrateOptions { SystemDatabaseConfigured = true }
            .Classify(jobStorePresent: true, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.RuntimeIncomplete);
    }
}
