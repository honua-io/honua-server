// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicPublicationCopierTests
{
    [UnitTest]
    public async Task CopyAsync_CompleteGeneration_ReturnsFourUrlsAfterPromotion()
    {
        var fixture = new Fixture();
        var result = await fixture.Copier.CopyAsync(fixture.Source, default);
        result.Should().HaveCount(4);
        fixture.Session.Verify(s => s.MarkCopyCompleteAsync(default), Times.Once);
        fixture.Destination.Verify(d => d.PutAsync(It.Is<PublicPublicationArtifact>(a =>
            a.Provenance != null && a.Provenance.ServiceId == "roads" && a.Provenance.MetadataRevision == 5), default), Times.Exactly(4));
    }

    [UnitTest]
    public async Task CopyAsync_OriginUploadFails_KeepsCleanupReservationWithoutPromotion()
    {
        var fixture = new Fixture();
        fixture.Destination.Setup(d => d.PutAsync(It.Is<PublicPublicationArtifact>(a => a.Key.FileName == "descriptor.json"), default))
            .ThrowsAsync(new IOException("origin unavailable"));
        await FluentActions.Awaiting(() => fixture.Copier.CopyAsync(fixture.Source, default)).Should().ThrowAsync<IOException>();
        fixture.Session.Verify(s => s.ReserveAsync(fixture.Source.Reservation, default), Times.Once);
        fixture.Session.Verify(s => s.MarkCopyCompleteAsync(default), Times.Never);
    }

    [UnitTest]
    public async Task CopyAsync_SourceChangesDuringCopy_DoesNotPromote()
    {
        var fixture = new Fixture();
        var current = true;
        var source = fixture.Source with { IsCurrentAndEligibleAsync = _ => Task.FromResult(current) };
        fixture.Destination.Setup(d => d.PutAsync(It.IsAny<PublicPublicationArtifact>(), default))
            .Callback(() => current = false).ReturnsAsync(new Uri("https://public.example/object"));
        await FluentActions.Awaiting(() => fixture.Copier.CopyAsync(source, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Session.Verify(s => s.MarkCopyCompleteAsync(default), Times.Never);
    }

    [UnitTest]
    public async Task CopyAsync_ReservationIsRevoking_RefusesLateOriginUpload()
    {
        var fixture = new Fixture();
        fixture.Session.Setup(s => s.ReadAsync(default)).ReturnsAsync(() => new PublicPublicationGeneration
        {
            Reservation = fixture.Source.Reservation, State = PublicPublicationState.Revoking, PurgeIds = []
        });
        await FluentActions.Awaiting(() => fixture.Copier.CopyAsync(fixture.Source, default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Destination.Verify(d => d.PutAsync(It.IsAny<PublicPublicationArtifact>(), default), Times.Never);
    }

    [UnitTest]
    public async Task CopyAsync_CrashBeforeReservationCommit_DoesNotUpload()
    {
        var fixture = new Fixture();
        fixture.Session.Setup(s => s.CommitAsync(default)).ThrowsAsync(new IOException("commit unknown"));
        await FluentActions.Awaiting(() => fixture.Copier.CopyAsync(fixture.Source, default)).Should().ThrowAsync<IOException>();
        fixture.Destination.Verify(d => d.PutAsync(It.IsAny<PublicPublicationArtifact>(), default), Times.Never);
    }

    private sealed class Fixture
    {
        public Mock<IPublicPublicationJournal> Journal { get; } = new();
        public Mock<IPublicPublicationSession> Session { get; } = new();
        public Mock<IPublicPublicationDestination> Destination { get; } = new();
        public PublicPublicationCopySource Source { get; }
        public PublicPublicationCopier Copier { get; }

        public Fixture()
        {
            PublicPublicationGeneration? generation = null;
            Source = new()
            {
                Reservation = new() { Key = new("publication", "version"), ServiceId = "roads", LayerIndex = 0, MetadataRevision = 5,
                    DestinationBucket = "public", DestinationBaseUrl = "https://public.example", CdnDistributionId = "distribution" },
                IsCurrentAndEligibleAsync = _ => Task.FromResult(true),
                OpenArtifactAsync = (_, _) => Task.FromResult<Stream?>(new MemoryStream([1, 2, 3]))
            };
            Session.Setup(s => s.ReadAsync(default)).ReturnsAsync(() => generation);
            Session.Setup(s => s.ReserveAsync(It.IsAny<PublicPublicationReservation>(), default))
                .Callback<PublicPublicationReservation, CancellationToken>((reservation, _) => generation = new()
                { Reservation = reservation, State = PublicPublicationState.Pending, PurgeIds = [] }).Returns(Task.CompletedTask);
            Journal.Setup(j => j.OpenAsync(Source.Reservation.Key, default)).ReturnsAsync(Session.Object);
            Destination.Setup(d => d.PutAsync(It.IsAny<PublicPublicationArtifact>(), default))
                .ReturnsAsync(new Uri("https://public.example/object"));
            Copier = new(Journal.Object, Destination.Object);
        }
    }
}
