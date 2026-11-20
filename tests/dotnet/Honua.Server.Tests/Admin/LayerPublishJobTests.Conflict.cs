// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ProgressStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;

namespace Honua.Server.Tests.Admin;

// A queued publish of an already published source table is refused at admission with the same
// conflict as the synchronous path, and a failed job never reports a layer it did not create.
public sealed partial class LayerPublishJobTests
{
    [UnitTheory]
    [InlineData((int)LayerPublishRunnerMode.DurableLocal)]
    [InlineData((int)LayerPublishRunnerMode.Unavailable)]
    public async Task Submit_AboveThresholdAlreadyPublished_IsConflictBeforeQueueing(int mode)
    {
        var publisher = LargeTablePublisher();
        publisher.FindPublishedSourceLayerAsync(
                Connection, Arg.Is<LayerPublishRequest>(request => request.Table == "roads"), Arg.Any<CancellationToken>())
            .Returns(42);
        var queue = Substitute.For<IJobQueue>();

        var conflict = await Assert.ThrowsAsync<LayerPublishingException>(() =>
            Service(publisher, (LayerPublishRunnerMode)mode, threshold: 10, queue: queue)
                .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), "roads", CancellationToken.None));

        Assert.Equal(LayerPublishingErrorKind.Conflict, conflict.ErrorKind);
        Assert.Equal("Layer already exists for table 'public.roads'.", conflict.Message);
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Submit_AboveThresholdEditableCopy_SkipsTheDuplicateCheck()
    {
        var publisher = LargeTablePublisher();
        publisher.FindPublishedSourceLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(42);
        var request = new LayerPublishRequest { Schema = "public", Table = "roads", LayerName = "Roads", CreateEditableCopy = true };

        var admission = await Service(publisher, LayerPublishRunnerMode.Unavailable, threshold: 10)
            .SubmitAsync(Connection, Guid.NewGuid(), request, "roads", CancellationToken.None);

        Assert.Equal(LayerPublishAdmissionKind.Unavailable, admission.Kind);
        await publisher.DidNotReceive().FindPublishedSourceLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Execute_ConflictFailure_ReportsNoStorageLayer()
    {
        const string jobId = "conflict-job";
        var store = new MemoryProgress();
        await store.SetProgressAsync(jobId, new LayerPublishJobProgress
        {
            OperationId = jobId,
            Status = ProgressStatus.Queued,
            RowsTotal = 5,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LayerPublishingException(
                LayerPublishingErrorKind.Conflict, "Layer already exists for table 'public.roads'.", 42));
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Connection);
        var payload = new LayerPublishJobPayload
        {
            ConnectionId = Guid.NewGuid(),
            ConnectionKey = "roads",
            RowCount = 5,
            Request = HttpRequest(),
        };

        await LayerPublishJobWork.ExecuteAsync(
            ScopeFactory(publisher, resolver), store, NullLogger<LayerPublishJobTests>.Instance,
            payload, jobId, context: null, CancellationToken.None);

        var progress = await store.GetProgressAsync<LayerPublishJobProgress>(jobId, CancellationToken.None);
        Assert.Equal(ProgressStatus.Failed, progress!.Status);
        Assert.Null(progress.LayerId);
        Assert.Contains("already exists", progress.ErrorMessage);
    }

    private static ILayerPublishingService LargeTablePublisher()
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(11L);
        return publisher;
    }
}
