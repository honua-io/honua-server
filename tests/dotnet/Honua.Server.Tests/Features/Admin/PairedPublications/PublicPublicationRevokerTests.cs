// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicPublicationRevokerTests
{
    [UnitTest]
    public async Task RevokeAsync_OneReceiptPending_DoesNotCompleteRevocation()
    {
        var fixture = new Fixture(["one", "two"]);
        fixture.Status.Setup(s => s.ReadAsync(new("distribution", "two"), default))
            .ReturnsAsync(new CdnInvalidationStatusResult(CdnInvalidationStatus.Pending));
        (await fixture.Revoker.RevokeAsync(fixture.Key, default)).Should().BeFalse();
        fixture.Session.Verify(s => s.RecordRevocationCompletedAsync(It.IsAny<PublicPublicationPurge>(), default), Times.Never);
    }

    [UnitTest]
    public async Task RevokeAsync_AllReceiptsComplete_RecordsOriginalDistributionAndFullSet()
    {
        var fixture = new Fixture(["one", "two"]);
        (await fixture.Revoker.RevokeAsync(fixture.Key, default)).Should().BeTrue();
        fixture.Session.Verify(s => s.RecordRevocationCompletedAsync(It.Is<PublicPublicationPurge>(p =>
            p.DistributionId == "distribution" && p.InvalidationIds.SequenceEqual(new[] { "one", "two" })), default), Times.Once);
        fixture.Destination.Verify(s => s.DeleteAsync(It.IsAny<PublicPublicationArtifactKey>(), default), Times.Exactly(4));
    }

    [UnitTest]
    public async Task RevokeAsync_ChangedOrigin_RefusesCleanupThroughNewDestination()
    {
        var fixture = new Fixture([]);
        var revoker = new PublicPublicationRevoker(fixture.Journal.Object, fixture.Target with { Bucket = "replacement" });
        await FluentActions.Awaiting(() => revoker.RevokeAsync(fixture.Key, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Destination.Verify(s => s.DeleteAsync(It.IsAny<PublicPublicationArtifactKey>(), default), Times.Never);
    }

    [UnitTest]
    public async Task RevokeAsync_SubmissionWithoutProviderIds_DoesNotRecordCompletion()
    {
        var fixture = new Fixture([]);
        fixture.Invalidator.Setup(s => s.InvalidateAsync(It.IsAny<IReadOnlyCollection<string>>(), default))
            .ReturnsAsync(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        await FluentActions.Awaiting(() => fixture.Revoker.RevokeAsync(fixture.Key, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Session.Verify(s => s.RecordRevocationCompletedAsync(It.IsAny<PublicPublicationPurge>(), default), Times.Never);
    }

    [Theory]
    [InlineData(PublicPublicationState.Pending, 1)]
    [InlineData(PublicPublicationState.Ready, 0)]
    [InlineData(PublicPublicationState.Revoking, 3)]
    [Trait("Tier", "Fast")]
    public async Task SweepAsync_ReadyCopiesRemainUntouched(PublicPublicationState state, int opens)
    {
        var fixture = new Fixture(["one"]);
        var generation = (await fixture.Session.Object.ReadAsync(default))! with { State = state };
        fixture.Journal.Setup(j => j.ReadOutstandingAsync(default)).ReturnsAsync([generation]);
        var sweep = new PublicPublicationRevocationSweep(fixture.Journal.Object, fixture.Revoker,
            NullLogger<PublicPublicationRevocationSweep>.Instance);

        await sweep.SweepAsync(default);

        fixture.Journal.Verify(j => j.OpenAsync(fixture.Key, default), Times.Exactly(opens));
    }

    [UnitTest]
    public async Task SweepAsync_AbandonedPendingCopy_RequestsDurableCleanup()
    {
        var fixture = new Fixture([]);
        var pending = (await fixture.Session.Object.ReadAsync(default))! with { State = PublicPublicationState.Pending };
        fixture.Session.Setup(s => s.ReadAsync(default)).ReturnsAsync(() => pending);
        fixture.Session.Setup(s => s.BeginRevocationAsync(default))
            .Callback(() => pending = pending with { State = PublicPublicationState.Revoking }).Returns(Task.CompletedTask);
        fixture.Journal.Setup(j => j.ReadOutstandingAsync(default)).ReturnsAsync([pending]);
        var sweep = new PublicPublicationRevocationSweep(fixture.Journal.Object, fixture.Revoker,
            NullLogger<PublicPublicationRevocationSweep>.Instance);

        await sweep.SweepAsync(default);

        fixture.Session.Verify(s => s.BeginRevocationAsync(default), Times.AtLeastOnce);
        fixture.Destination.Verify(s => s.DeleteAsync(It.IsAny<PublicPublicationArtifactKey>(), default), Times.Exactly(4));
    }

    [UnitTest]
    public async Task SweepAsync_PendingCopyPromotedBeforeLock_DoesNotRevokeReadyGeneration()
    {
        var fixture = new Fixture([]);
        var pending = (await fixture.Session.Object.ReadAsync(default))! with { State = PublicPublicationState.Pending };
        fixture.Journal.Setup(j => j.ReadOutstandingAsync(default)).ReturnsAsync([pending]);
        var sweep = new PublicPublicationRevocationSweep(fixture.Journal.Object, fixture.Revoker,
            NullLogger<PublicPublicationRevocationSweep>.Instance);

        await sweep.SweepAsync(default);

        fixture.Session.Verify(s => s.BeginRevocationAsync(default), Times.Never);
        fixture.Destination.Verify(s => s.DeleteAsync(It.IsAny<PublicPublicationArtifactKey>(), default), Times.Never);
    }

    [UnitTest]
    public async Task SweepAsync_FailedGeneration_DoesNotLoseOtherCleanupRequests()
    {
        var fixture = new Fixture([]);
        var first = (await fixture.Session.Object.ReadAsync(default))! with { State = PublicPublicationState.Revoking };
        var secondKey = new PublicPublicationKey("another", "version");
        var second = first with { Reservation = first.Reservation with { Key = secondKey } };
        fixture.Journal.Setup(j => j.ReadOutstandingAsync(default)).ReturnsAsync([first, second]);
        fixture.Journal.Setup(j => j.OpenAsync(It.IsAny<PublicPublicationKey>(), default))
            .ThrowsAsync(new IOException("Unavailable origin"));
        var sweep = new PublicPublicationRevocationSweep(fixture.Journal.Object, fixture.Revoker,
            NullLogger<PublicPublicationRevocationSweep>.Instance);

        await sweep.SweepAsync(default);

        fixture.Journal.Verify(j => j.OpenAsync(fixture.Key, default), Times.Once);
        fixture.Journal.Verify(j => j.OpenAsync(secondKey, default), Times.Once);
    }

    private sealed class Fixture
    {
        public PublicPublicationKey Key { get; } = new("publication", "version");
        public Mock<IPublicPublicationJournal> Journal { get; } = new();
        public Mock<IPublicPublicationSession> Session { get; } = new();
        public Mock<IPublicPublicationDestination> Destination { get; } = new();
        public Mock<ICdnInvalidator> Invalidator { get; } = new();
        public Mock<ICdnInvalidationStatusReader> Status { get; } = new();
        public PublicPublicationCleanupTarget Target { get; }
        public PublicPublicationRevoker Revoker { get; }

        public Fixture(string[] receipts)
        {
            var generation = new PublicPublicationGeneration
            {
                Reservation = new() { Key = Key, ServiceId = "roads", LayerIndex = 0, MetadataRevision = 1,
                    DestinationBucket = "original", DestinationBaseUrl = "https://public.example", CdnDistributionId = "distribution" },
                State = PublicPublicationState.Ready, PurgeIds = receipts
            };
            Session.Setup(s => s.ReadAsync(default)).ReturnsAsync(() => generation);
            Session.Setup(s => s.BeginRevocationAsync(default)).Callback(() => generation = generation with { State = PublicPublicationState.Revoking }).Returns(Task.CompletedTask);
            Journal.Setup(j => j.OpenAsync(Key, default)).ReturnsAsync(Session.Object);
            Status.Setup(s => s.ReadAsync(It.IsAny<CdnInvalidationReceipt>(), default)).ReturnsAsync(new CdnInvalidationStatusResult(CdnInvalidationStatus.Completed));
            Invalidator.Setup(s => s.InvalidateAsync(It.IsAny<IReadOnlyCollection<string>>(), default))
                .ReturnsAsync(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "one,two"));
            Target = new() { Bucket = "original", BaseUrl = "https://public.example", DistributionId = "distribution",
                Destination = Destination.Object, Invalidator = Invalidator.Object, StatusReader = Status.Object };
            Revoker = new(Journal.Object, Target);
        }
    }
}
