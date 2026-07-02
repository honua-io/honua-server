// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Regression guard for honua-server#2350: a standalone Test image (e.g. the honua-console
/// live lane's Testcontainers server) that enables the Operate observability fixture must
/// register the real data-provider infrastructure the seeder resolves, otherwise
/// <c>POST /api/v1/admin/dev/fixtures/operate-observability/{profile}</c> 500s with
/// "No service for type 'IAlertAdminStore' has been registered.". In-process WebAppFixture
/// hosts (which skip migrations and wire their own providers) must stay excluded.
/// </summary>
public sealed class TestInfrastructureRegistrationPolicyTests
{
    [Fact]
    public void ShouldRegisterInfrastructure_OutsideTestEnvironment_AlwaysRegisters()
    {
        TestInfrastructureRegistrationPolicy.ShouldRegisterInfrastructure(
                isTestEnvironment: false,
                explicitTestOptIn: false,
                operateObservabilityFixtureEnabled: false,
                hostManagesOwnMigrations: false)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ShouldRegisterInfrastructure_TestEnvironmentWithoutOptIn_SkipsForWebAppFixture()
    {
        // WebAppFixture host: skips migrations (hostManagesOwnMigrations = false) and wires its
        // own isolated providers, so the composition root must stay skipped even when the
        // fixture endpoint is enabled (OperateObservabilityFixtureEndpointsTests scenario).
        TestInfrastructureRegistrationPolicy.ShouldRegisterInfrastructure(
                isTestEnvironment: true,
                explicitTestOptIn: false,
                operateObservabilityFixtureEnabled: true,
                hostManagesOwnMigrations: false)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ShouldRegisterInfrastructure_TestEnvironmentWithExplicitOptIn_Registers()
    {
        TestInfrastructureRegistrationPolicy.ShouldRegisterInfrastructure(
                isTestEnvironment: true,
                explicitTestOptIn: true,
                operateObservabilityFixtureEnabled: false,
                hostManagesOwnMigrations: false)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ShouldRegisterInfrastructure_StandaloneTestImageWithFixtureEnabled_Registers()
    {
        // honua-server#2350: standalone Test image that runs its own migrations and enables the
        // Operate observability fixture must register the providers the seeder depends on.
        TestInfrastructureRegistrationPolicy.ShouldRegisterInfrastructure(
                isTestEnvironment: true,
                explicitTestOptIn: false,
                operateObservabilityFixtureEnabled: true,
                hostManagesOwnMigrations: true)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ShouldRegisterInfrastructure_TestImageWithoutFixture_Skips()
    {
        TestInfrastructureRegistrationPolicy.ShouldRegisterInfrastructure(
                isTestEnvironment: true,
                explicitTestOptIn: false,
                operateObservabilityFixtureEnabled: false,
                hostManagesOwnMigrations: true)
            .Should()
            .BeFalse();
    }
}
