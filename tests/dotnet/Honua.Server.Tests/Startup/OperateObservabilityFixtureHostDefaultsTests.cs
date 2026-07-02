// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using FluentAssertions;
using Honua.Server.Features.Admin.OperateFixtures;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Regression guard for honua-server#2350: a standalone Operate observability fixture host that
/// supplies no connection-encryption key must receive deterministic Development/Test defaults so
/// the Postgres secure-connection provider (and therefore the seed endpoint) works. Explicitly
/// configured values must always win.
/// </summary>
public sealed class OperateObservabilityFixtureHostDefaultsTests
{
    [Fact]
    public void CreateMissingDefaults_WhenUnset_SuppliesMasterKeyAndSalt()
    {
        var configuration = new ConfigurationBuilder().Build();

        var defaults = OperateObservabilityFixtureHostDefaults.CreateMissingDefaults(configuration);

        defaults.Should().ContainKey(OperateObservabilityFixtureHostDefaults.MasterKeyConfigurationPath);
        defaults.Should().ContainKey(OperateObservabilityFixtureHostDefaults.SaltConfigurationPath);
        defaults[OperateObservabilityFixtureHostDefaults.MasterKeyConfigurationPath]
            .Should().NotBeNullOrWhiteSpace();
        // ConnectionEncryptionService enforces a 32-character minimum on the master key.
        defaults[OperateObservabilityFixtureHostDefaults.MasterKeyConfigurationPath]!
            .Length.Should().BeGreaterThanOrEqualTo(32);
    }

    [Fact]
    public void CreateMissingDefaults_WhenConfigured_DoesNotOverrideOperatorValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperateObservabilityFixtureHostDefaults.MasterKeyConfigurationPath] =
                    "operator-supplied-master-key-value-0001",
                [OperateObservabilityFixtureHostDefaults.SaltConfigurationPath] =
                    "b3BlcmF0b3Itc3VwcGxpZWQtc2FsdA==",
            })
            .Build();

        var defaults = OperateObservabilityFixtureHostDefaults.CreateMissingDefaults(configuration);

        defaults.Should().BeEmpty();
    }

    [Fact]
    public void CreateMissingDefaults_WhenOnlySaltConfigured_SuppliesOnlyMasterKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperateObservabilityFixtureHostDefaults.SaltConfigurationPath] =
                    "b3BlcmF0b3Itc3VwcGxpZWQtc2FsdA==",
            })
            .Build();

        var defaults = OperateObservabilityFixtureHostDefaults.CreateMissingDefaults(configuration);

        defaults.Should().ContainKey(OperateObservabilityFixtureHostDefaults.MasterKeyConfigurationPath);
        defaults.Should().NotContainKey(OperateObservabilityFixtureHostDefaults.SaltConfigurationPath);
    }
}
