// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="MigrationSafetyOptions"/> defaults — the safe single-node upgrade policy
/// (#2565) gated closed by default (#2812) so a schema-narrowing migration is never applied unattended.
/// </summary>
public sealed class MigrationSafetyOptionsTests
{
    [UnitTest]
    public void Defaults_GateContractMigrationsClosed()
    {
        var options = new MigrationSafetyOptions();

        options.Enforce.Should().BeTrue("unannotated contract migrations stay fail-closed by default");
        options.ContractApplyPolicy.Should().Be(ContractApplyPolicy.Gate,
            "annotated contract migrations on an existing database require explicit approval by default (#2812)");
        options.BackupCommand.Should().BeNull("the pre-migration backup hook is opt-in");
    }
}
