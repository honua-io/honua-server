// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Edit.Receipts;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.Features.Edit;

public sealed class IdempotencyPayloadStoreTests
{
    [UnitTest]
    public async Task TryReserveAsync_StoreUnavailable_ReportsFailureAndReturnsReleaseToken()
    {
        var reservations = Substitute.For<IIdempotencyReservationStore>();
        var failure = new InvalidOperationException("unavailable");
        reservations.TryReserveAsync("key", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IdempotencyReservation?>(failure));
        var store = new IdempotencyPayloadStore(reservations);
        var failures = new List<Exception>();
        var token = await store.TryReserveAsync(new("key", TimeSpan.FromMinutes(1)), failures.Add, CancellationToken.None);
        token.Should().NotBeNullOrEmpty();
        failures.Should().ContainSingle().Which.Should().BeSameAs(failure);
    }

    [UnitTest]
    public async Task TryReserveAsync_Cancellation_PropagatesWithoutReportingStoreFailure()
    {
        var reservations = Substitute.For<IIdempotencyReservationStore>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        reservations.TryReserveAsync("key", Arg.Any<TimeSpan>(), cancellation.Token)
            .Returns(Task.FromCanceled<IdempotencyReservation?>(cancellation.Token));
        var store = new IdempotencyPayloadStore(reservations);
        var failures = new List<Exception>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.TryReserveAsync(new("key", TimeSpan.FromMinutes(1)), failures.Add, cancellation.Token));
        failures.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ReleaseAsync_CancelledRequest_StillReleasesOnlyItsReservation()
    {
        var reservations = Substitute.For<IIdempotencyReservationStore>();
        var store = new IdempotencyPayloadStore(reservations);
        var reservation = new IdempotencyReservation("key", "owner");
        await store.ReleaseAsync(reservation, _ => throw new InvalidOperationException("unexpected failure"));
        await reservations.Received(1).ReleaseAsync(reservation, CancellationToken.None);
    }
}
