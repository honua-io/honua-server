// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;

namespace Honua.Server.Tests.Startup;

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
