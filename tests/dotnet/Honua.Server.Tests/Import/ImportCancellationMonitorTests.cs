// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class ImportCancellationMonitorTests
{
    [UnitTest]
    public async Task MonitorCancellationAsync_ProgressReadsFailAndLeaseIsLost_CancelsImport()
    {
        var manager = Substitute.For<IImportWorkerJobManager<GeoservicesImportRequest, GeoservicesImportProgress>>();
        var leader = Substitute.For<IDistributedLeaderElection>();
        var progress = Substitute.For<IDistributedProgressStore<GeoservicesImportProgress>>();
        manager.LeaderElection.Returns(leader);
        manager.ProgressStore.Returns(progress);
        leader.HeartbeatAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        progress.GetProgressAsync("job", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GeoservicesImportProgress?>(new InvalidOperationException("progress unavailable")));
        using var jobCancellation = new CancellationTokenSource();
        using var monitorCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var leadershipLost = false;

        await ImportBackgroundServiceCoordinator.MonitorCancellationAsync(
            "job", manager, jobCancellation, current => current.Status == GeoservicesImportStatus.Cancelled,
            () => leadershipLost = true, NullLogger.Instance, (_, _) => { }, (_, _, _) => { },
            monitorCancellation.Token);

        jobCancellation.IsCancellationRequested.Should().BeTrue();
        leadershipLost.Should().BeTrue();
    }
}
