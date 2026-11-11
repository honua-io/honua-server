// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Infrastructure.Security;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresIdempotencyReservationStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _reservationWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _receiptRetention = TimeSpan.FromHours(24);
    private static readonly byte[] _receipt = Encoding.UTF8.GetBytes("{\"addResults\":[]}");

    [IntegrationTest]
    public async Task TryReserveAsync_TwoNodes_ExactlyOneReserves()
    {
        await using var harness = await CreateHarnessAsync();
        var scopeKey = NewScopeKey();

        var reservations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.CreateIdempotencyReservationStore().TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None)));

        reservations.Count(reservation => reservation is not null).Should().Be(1);
        (await harness.CreateIdempotencyReservationStore().FindReceiptAsync(scopeKey, CancellationToken.None))
            .Should().BeNull("a pending reservation has no receipt to replay");
    }

    [IntegrationTest]
    public async Task CompleteAsync_ByTheOwner_StoresTheReceiptForEveryNode()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateIdempotencyReservationStore();
        var scopeKey = NewScopeKey();
        var reservation = await store.TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None);

        (await store.CompleteAsync(reservation!.Value, _receipt, _receiptRetention, CancellationToken.None)).Should().BeTrue();

        var otherNode = harness.CreateIdempotencyReservationStore();
        (await otherNode.FindReceiptAsync(scopeKey, CancellationToken.None)).Should().Equal(_receipt);
        (await otherNode.TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task CompleteAsync_ByANonOwner_IsRefused()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateIdempotencyReservationStore();
        var scopeKey = NewScopeKey();
        var lapsed = await store.TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None);
        await harness.ExecuteAsync($"UPDATE {harness.Table("idempotency_reservations")} SET expires_at = now() - interval '1 second'");
        var current = await store.TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None);

        (await store.CompleteAsync(lapsed!.Value, _receipt, _receiptRetention, CancellationToken.None)).Should().BeFalse();
        (await store.CompleteAsync(current!.Value, _receipt, _receiptRetention, CancellationToken.None)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task ReleaseAsync_FreesAPendingReservationButNeverACompletedOne()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateIdempotencyReservationStore();
        var scopeKey = NewScopeKey();
        var abandoned = await store.TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None);

        await store.ReleaseAsync(abandoned!.Value, CancellationToken.None);
        var completed = (await store.TryReserveAsync(scopeKey, _reservationWindow, CancellationToken.None))!.Value;
        await store.CompleteAsync(completed, _receipt, _receiptRetention, CancellationToken.None);
        await store.ReleaseAsync(completed, CancellationToken.None);

        (await store.FindReceiptAsync(scopeKey, CancellationToken.None)).Should().Equal(_receipt);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresIdempotencyReservationStoreTests));

    private static string NewScopeKey() => "featureserver:applyedits:idem:service:0:caller:" + Guid.NewGuid().ToString("N");
}
