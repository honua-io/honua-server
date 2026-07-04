// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.HealthCheck;

/// <summary>
/// Unit tests for <see cref="ProductionHealthChecks.AddProductionHealthChecks"/>. Covers PA-065 /
/// PA-162: the <c>ExternalServiceHealthCheck</c> no-op stub (always returned <c>Healthy</c>
/// regardless of real external-service state) was removed rather than registered as a real
/// health gate, since it manufactured false confidence.
/// </summary>
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
}
