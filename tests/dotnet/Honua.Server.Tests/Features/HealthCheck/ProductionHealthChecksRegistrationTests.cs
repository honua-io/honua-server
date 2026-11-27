// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Honua.Server.Tests.Features.HealthCheck;

[Protocol(TestProtocols.TestQuality)]
public sealed class ProductionHealthChecksRegistrationTests
{
    [UnitTest]
    public void AddProductionHealthChecks_DoesNotRegisterExternalServiceStub()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddProductionHealthChecks(configuration);

        using var provider = services.BuildServiceProvider();
        var registrationNames = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Select(registration => registration.Name)
            .ToArray();

        registrationNames.Should().NotContain("external-services");
        registrationNames.Should().Contain(new[]
        {
            "file-upload",
            "production-metrics",
            "feature-change-outbox",
            "plugins",
        });
    }

    [UnitTest]
    public void DatabaseHealthCheck_CreateProbeCommand_SetsExplicitFiveSecondTimeout()
    {
        using var connection = new NpgsqlConnection();
        using var command = DatabaseHealthCheck.CreateProbeCommand(connection);

        command.CommandText.Should().Be("SELECT 1");
        command.CommandTimeout.Should().Be(DatabaseHealthCheck.ProbeCommandTimeoutSeconds);
        command.CommandTimeout.Should().Be(5);
    }
}
