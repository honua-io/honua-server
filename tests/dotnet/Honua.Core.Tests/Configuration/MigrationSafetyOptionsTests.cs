// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="MigrationSafetyOptions"/> defaults — the safe single-node upgrade policy
/// (#2565) must preserve today's behavior out of the box.
/// </summary>
public sealed class MigrationSafetyOptionsTests
{
    [UnitTest]
    public void Defaults_PreserveTodaysBehavior()
    {
        var options = new MigrationSafetyOptions();

        options.Enforce.Should().BeTrue("unannotated contract migrations stay fail-closed by default");
        options.ContractApplyPolicy.Should().Be(ContractApplyPolicy.Auto,
            "annotated contract migrations apply automatically by default (no gate)");
        options.BackupCommand.Should().BeNull("the pre-migration backup hook is opt-in");
    }
}
