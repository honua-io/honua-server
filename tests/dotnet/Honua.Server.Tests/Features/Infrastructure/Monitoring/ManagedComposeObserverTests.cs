// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>Live target readback must never substitute desired configuration for observed state.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class ManagedComposeObserverTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_BeforeUpgradeAndAfterRollback_ReportsLivePriorRevisionAndSkew()
    {
        var options = Configuration();
        var live = Target("sha256:" + new string('b', 64));
        var result = ManagedComposeObserver.ProjectRelease(options, [live]);
        var serving = Assert.Single(result.Serving);
        Assert.Equal(live.RunningImage, serving.ObservedArtifactReference);
        Assert.Equal("2026.1-rc.1", serving.ServerVersion);
        Assert.Equal(options.PlatformRelease.ServingArtifactReference, serving.EffectiveArtifactReference);
        Assert.True(serving.Skewed);
        Assert.False(result.IsCoVersioned);
        Assert.Contains("compose-local", result.SkewedIds);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_MatchingLiveDigestAndVersion_ReportsConverged()
    {
        var options = Configuration();
        var result = ManagedComposeObserver.ProjectRelease(options, [Target(options.PlatformRelease.ServingArtifactReference!)]);
        Assert.True(result.IsCoVersioned);
        Assert.False(Assert.Single(result.Serving).Skewed);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ProjectRelease_MissingReadback_DoesNotClaimConfiguredImageIsServing()
    {
        var result = ManagedComposeObserver.ProjectRelease(Configuration(), []);
        Assert.Null(Assert.Single(result.Serving).ObservedArtifactReference);
        Assert.Equal("notObserved", result.Serving[0].ObservationStatus);
        Assert.False(result.IsCoVersioned);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, result.EvidencePosture!.Sources[0].Completeness);
        Assert.Null(result.EvidencePosture.Sources[0].ObservedAt);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_ExecutorUnavailable_ReturnsUnknownWithoutInventingServiceFailures()
    {
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ComposeExecutorResponse>>(_ => throw new IOException("unavailable"));
        var observer = CreateObserver(client, Configuration());
        var target = Assert.Single(await observer.ReadAsync(CancellationToken.None));
        Assert.False(target.Available);
        Assert.Equal("Unknown", target.Status);
        Assert.Null(target.RunningImage);
        Assert.Empty(target.Services);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_UnboundLocalTarget_DoesNotReadAnotherTargetsExecutor()
    {
        var client = Substitute.For<IComposeExecutorClient>();
        var options = Configuration();
        options.DeployTargets[0].TargetId = "another-target";
        var target = Assert.Single(await CreateObserver(client, options).ReadAsync(CancellationToken.None));
        Assert.False(target.Available);
        await client.DidNotReceive().ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_StoppedService_IsCompleteActionableEvidenceOfAnOutage()
    {
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(new ComposeExecutorResponse
        {
            Ok = true, ReadbackComplete = false,
            Services = [new() { Service = "redis", Status = "Unhealthy", Description = "The managed service is stopped." }]
        });
        var targets = await CreateObserver(client, Configuration()).ReadAsync(CancellationToken.None);
        Assert.Equal("Unhealthy", Assert.Single(targets).Status);
        Assert.True(EvidencePostureFactory.IsActionable(ManagedComposeObserver.Source(targets)));
    }

    private static ManagedComposeObserver CreateObserver(IComposeExecutorClient client, ControlPlaneOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
        monitor.CurrentValue.Returns(options);
        return new(client, Options.Create(new ComposeExecutorOptions()), monitor, TimeProvider.System);
    }

    private static ControlPlaneOptions Configuration() => new()
    {
        PlatformRelease = new() { Version = "2026.1-rc.2", ServingArtifactReference = "sha256:" + new string('a', 64) },
        DeployTargets = [new() { TargetId = "compose-local", TargetKind = DeployTargetKind.LocalCompose,
            Backend = ComposeDeployBackend.AdapterBackendName }]
    };

    private static OpsManagedTargetHealthView Target(string image) => new()
    {
        TargetId = "compose-local", Available = true, Status = "Healthy", ObservedAt = DateTimeOffset.UtcNow,
        RunningImage = image, ImageId = image, ServerVersion = "2026.1-rc.1"
    };
}
