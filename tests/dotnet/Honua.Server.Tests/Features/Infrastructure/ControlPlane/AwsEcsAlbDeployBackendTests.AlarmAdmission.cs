// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class AwsEcsAlbDeployBackendTests
{
    [UnitTest]
    public async Task PlanAsync_AlarmAdmissionBlocked_ReportsRetryableWarning()
    {
        var (backend, admission, clients) = CreateAlarmBlockedBackend();

        var plan = await backend.PlanAsync(CreateSpec());

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().BeEmpty();
        plan.Warnings.Should().Contain("Deployment alarm admission is blocked.");
        await admission.Received(1).GetBlockingReasonAsync("prod-ecs", Arg.Any<CancellationToken>());
        clients.Alb.ReceivedCalls().Should().BeEmpty();
        clients.Ecs.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("start")]
    [InlineData("promote")]
    [InlineData("prepare")]
    [InlineData("activate")]
    [InlineData("complete")]
    public async Task ExecuteAsync_AlarmAdmissionBlocked_RefusesBeforeProviderCalls(string boundary)
    {
        var (backend, admission, clients) = CreateAlarmBlockedBackend();
        var operation = CreateOperation();
        if (boundary == "complete")
        {
            operation = operation with
            {
                Deploy = operation.Deploy! with
                {
                    DesiredRevision = "registry.example/honua/server@sha256:" + new string('a', 64)
                }
            };
        }
        Func<Task> execute = boundary switch
        {
            "start" => async () => { await backend.StartAsync(operation); },
            "promote" => async () => { await backend.PromoteAsync(operation); },
            "prepare" => async () => { await backend.EnsurePreparationAdmittedAsync(operation, default); },
            "complete" => async () => { await backend.CompleteProtectionAsync(operation); },
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
