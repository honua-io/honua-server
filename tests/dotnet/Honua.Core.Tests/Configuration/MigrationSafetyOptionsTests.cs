// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="MigrationSafetyOptions"/> defaults — the safe single-node upgrade policy
/// (#2565) gated closed by default (#2812) so a schema-narrowing migration is never applied unattended.
/// </summary>
public sealed class MigrationSafetyOptionsTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    public void Bind_UndefinedPolicy_RefusesBeforeMigrationRunnerCanBeConstructed(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{MigrationSafetyOptions.SectionName}:ContractApplyPolicy"] = value,
            }).Build();

        var bind = () => configuration.GetSection(MigrationSafetyOptions.SectionName).Get<MigrationSafetyOptions>();

        bind.Should().Throw<Exception>().Which.GetBaseException()
            .Should().BeOfType<ArgumentOutOfRangeException>().Which.ParamName
            .Should().Be(nameof(MigrationSafetyOptions.ContractApplyPolicy));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("Gate", ContractApplyPolicy.Gate)]
    [InlineData("1", ContractApplyPolicy.Gate)]
    [InlineData("Auto", ContractApplyPolicy.Auto)]
    [InlineData("0", ContractApplyPolicy.Auto)]
    public void Bind_DefinedPolicy_PreservesExplicitChoice(string value, ContractApplyPolicy expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ContractApplyPolicy"] = value }).Build();

        configuration.Get<MigrationSafetyOptions>()!.ContractApplyPolicy.Should().Be(expected);
    }

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
