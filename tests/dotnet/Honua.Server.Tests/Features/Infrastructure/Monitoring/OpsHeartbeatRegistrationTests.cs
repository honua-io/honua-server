// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsHeartbeatRegistrationTests
{
    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [Operation(Operations.TestInfrastructure)]
    public void AddOpsHeartbeat_StackMissing_DoesNotRegisterWorker(string? stack)
    {
        var configuration = Configuration(stack);
        var services = new ServiceCollection();

        services.AddOpsHeartbeat(configuration);

        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
    }

    [UnitTheory]
    [InlineData("1invalid")]
    [InlineData("stack/name")]
    [Operation(Operations.TestInfrastructure)]
    public void AddOpsHeartbeat_InvalidStack_RejectsConfiguration(string stack)
    {
        var services = new ServiceCollection();
        var register = () => services.AddOpsHeartbeat(Configuration(stack));

        register.Should().Throw<InvalidOperationException>().WithMessage("*Observability:Heartbeat:StackName*");
    }

    private static IConfiguration Configuration(string? stack) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:Heartbeat:StackName"] = stack })
        .Build();
}
#endif
