// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class AwsEcsAlbDeployBackendTests
{
    [Fact]
    public async Task PlanAsync_AlarmAdmissionBlocked_ReportsBlockingReason()
    {
        var (backend, admission, clients) = CreateAlarmBlockedBackend();

        var plan = await backend.PlanAsync(CreateSpec());

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain("Deployment alarm admission is blocked.");
        await admission.Received(1).GetBlockingReasonAsync("prod-ecs", Arg.Any<CancellationToken>());
        clients.Alb.ReceivedCalls().Should().BeEmpty();
        clients.Ecs.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("start")]
    [InlineData("promote")]
    [InlineData("prepare")]
    [InlineData("activate")]
    public async Task ExecuteAsync_AlarmAdmissionBlocked_RefusesBeforeProviderCalls(string boundary)
    {
        var (backend, admission, clients) = CreateAlarmBlockedBackend();
        var operation = CreateOperation();
        Func<Task> execute = boundary switch
        {
            "start" => async () => { await backend.StartAsync(operation); },
            "promote" => async () => { await backend.PromoteAsync(operation); },
            "prepare" => async () => { await backend.EnsurePreparationAdmittedAsync(operation, default); },
            _ => async () => { await backend.ActivateCheckpointAsync(operation, default); }
        };

        await execute.Should().ThrowExactlyAsync<DeployAdmissionBlockedException>()
            .WithMessage("Deployment alarm admission is blocked.");
        await admission.Received(1).GetBlockingReasonAsync("prod-ecs", Arg.Any<CancellationToken>());
        clients.Alb.ReceivedCalls().Should().BeEmpty();
        clients.Ecs.ReceivedCalls().Should().BeEmpty();
    }

    private static (AwsEcsAlbDeployBackend Backend, IAwsDeploymentAlarmAdmission Admission, AwsEcsAlbDeployClients Clients) CreateAlarmBlockedBackend()
    {
        var admission = Substitute.For<IAwsDeploymentAlarmAdmission>();
        admission.GetBlockingReasonAsync("prod-ecs", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("Deployment alarm admission is blocked."));
        var clients = new AwsEcsAlbDeployClients(Substitute.For<IAwsAlbClient>(), Substitute.For<IAwsEcsClient>());
        var backend = new AwsEcsAlbDeployBackend(
            clients,
            NullLogger<AwsEcsAlbDeployBackend>.Instance, alarmAdmission: admission);
        return (backend, admission, clients);
    }
}
