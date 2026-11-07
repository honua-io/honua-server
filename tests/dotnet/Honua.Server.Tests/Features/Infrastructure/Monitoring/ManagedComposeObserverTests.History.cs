// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed partial class ManagedComposeObserverTests
{
    [UnitTheory]
    [InlineData(ManagedComposeObserver.SourceId)]
    [InlineData(EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_ExecutorLostAfterSuccess_RetainsHistoryWithoutReusingLiveData(string sourceId)
    {
        var options = Configuration();
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(ReadyResponse(options)),
            Task.FromException<ComposeExecutorResponse>(new IOException("Executor unavailable")));
        var observer = CreateObserver(client, options);
        var first = SourceSnapshot(sourceId, options, await observer.ReadAsync(CancellationToken.None));
        Assert.True(EvidencePostureFactory.IsActionable(first));
        Assert.NotNull(first.LastSuccessfulAt);

        var targets = await observer.ReadAsync(CancellationToken.None);
        var failed = SourceSnapshot(sourceId, options, targets);

        Assert.Equal(first.LastSuccessfulAt, failed.LastSuccessfulAt);
        Assert.Equal(first.ObservedAt, failed.ObservedAt);
        Assert.DoesNotContain(EvidencePostureVocabulary.ReasonCodes.NeverSucceeded, failed.ReasonCodes);
        Assert.DoesNotContain(EvidencePostureVocabulary.ReasonCodes.MissingObservationTime, failed.ReasonCodes);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.SourceUnavailable, failed.ReasonCodes);
        Assert.False(EvidencePostureFactory.IsActionable(failed));
        var target = Assert.Single(targets);
        Assert.False(target.Available);
        Assert.Null(target.RunningImage);
        Assert.Null(target.ServerVersion);
        Assert.Empty(target.Services);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_TargetUnreadyAfterSuccess_DistinguishesItFromExecutorLoss()
    {
        var options = Configuration();
        var ready = ReadyResponse(options);
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(
            ready,
            new ComposeExecutorResponse
            {
                Ok = true,
                RunningImage = ready.RunningImage,
                ImageId = ready.ImageId,
                Services = [new() { Service = "honua", Status = "Unhealthy", Description = "Unready" }]
            });
        var observer = CreateObserver(client, options);
        var first = SourceSnapshot(EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus,
            options, await observer.ReadAsync(CancellationToken.None));
        Assert.True(EvidencePostureFactory.IsActionable(first));

        var targets = await observer.ReadAsync(CancellationToken.None);
        var release = SourceSnapshot(EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus, options, targets);

        Assert.True(Assert.Single(targets).Available);
        Assert.Equal("Unhealthy", targets[0].Status);
        Assert.True(EvidencePostureFactory.IsActionable(SourceSnapshot(ManagedComposeObserver.SourceId, options, targets)));
        Assert.Equal(first.LastSuccessfulAt, release.LastSuccessfulAt);
        Assert.DoesNotContain(EvidencePostureVocabulary.ReasonCodes.NeverSucceeded, release.ReasonCodes);
        Assert.DoesNotContain(EvidencePostureVocabulary.ReasonCodes.SourceUnavailable, release.ReasonCodes);
        Assert.Contains("targetUnready", release.ReasonCodes);
        Assert.False(EvidencePostureFactory.IsActionable(release));
        Assert.Null(targets[0].ServerVersion);
    }

    [UnitTheory]
    [InlineData(ManagedComposeObserver.SourceId)]
    [InlineData(EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadAsync_ConfiguredTargetChanges_DoesNotReuseAnotherTargetsHistory(string sourceId)
    {
        var options = Configuration();
        var client = Substitute.For<IComposeExecutorClient>();
        client.ExecuteAsync(Arg.Any<ComposeExecutorRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(ReadyResponse(options)),
            Task.FromException<ComposeExecutorResponse>(new IOException("Executor unavailable")));
        var observer = CreateObserver(client, options);
        Assert.True(EvidencePostureFactory.IsActionable(SourceSnapshot(sourceId, options,
            await observer.ReadAsync(CancellationToken.None))));

        options.DeployTargets[0].TargetId = "another-target";
        var changed = SourceSnapshot(sourceId, options, await observer.ReadAsync(CancellationToken.None));
        Assert.Null(changed.LastSuccessfulAt);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.NeverSucceeded, changed.ReasonCodes);
        options.DeployTargets[0].TargetId = "compose-local";
        var restored = SourceSnapshot(sourceId, options, await observer.ReadAsync(CancellationToken.None));
        Assert.Null(restored.LastSuccessfulAt);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.NeverSucceeded, restored.ReasonCodes);
    }

    private static ComposeExecutorResponse ReadyResponse(ControlPlaneOptions options) => new()
    {
        Ok = true,
        ReadbackComplete = true,
        RunningImage = options.PlatformRelease.ServingArtifactReference,
        ImageId = options.PlatformRelease.ServingArtifactReference,
        ServerVersion = "2026.1-rc.2",
        Services = [new() { Service = "honua", Status = "Healthy", Description = "Ready" }]
    };

    private static EvidenceSourceEnvelope SourceSnapshot(string sourceId, ControlPlaneOptions options,
        IReadOnlyList<OpsManagedTargetHealthView> targets)
        => sourceId == ManagedComposeObserver.SourceId
            ? Assert.Single(EvidencePostureFactory.Build(DateTimeOffset.UtcNow, ManagedComposeObserver.Source(targets)).Sources)
            : Assert.Single(ManagedComposeObserver.ProjectRelease(options, targets, sourceId).EvidencePosture!.Sources);
}
