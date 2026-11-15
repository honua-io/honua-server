// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Infrastructure.Collaboration;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Security)]
public sealed class PostgresFeatureLockServiceTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private readonly string _schema = "feature_locks_" + Guid.NewGuid().ToString("N");
    private static readonly FeatureRef _feature = FeatureRef.Canonical("Map", 1, "001");
    private static readonly LockHolder _holder = new("editor", "Editor", "session", "tenant", "principal");
    private static readonly FeatureLockAccessContext _write = FeatureLockAccessContext.AuthorizedWrite;

    public async Task InitializeAsync()
    {
        await database.CreateSchemaUnderLockAsync(_schema);
        await using var stream = typeof(PostgresFeatureLockService).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.180_CreateFeatureLocks.sql")
            ?? throw new InvalidOperationException("Feature lock migration is missing.");
        using var reader = new StreamReader(stream);
        await database.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", new NpgsqlCommandBuilder().QuoteIdentifier(_schema), StringComparison.Ordinal));
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    public async Task ClaimAsync_ConcurrentNodes_ExactlyOneHolderWins()
    {
        var first = Store();
        var second = Store();
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            (index % 2 == 0 ? first : second).ClaimAsync(_feature,
                _holder with { SessionId = Guid.NewGuid().ToString("N") }, TimeSpan.FromMinutes(1), _write).AsTask()));
        claims.Count(claim => claim.Status == FeatureLockClaimStatus.Claimed).Should().Be(1);
        claims.Count(claim => claim.Status == FeatureLockClaimStatus.HeldByOther).Should().Be(19);
        var lease = claims.Single(claim => claim.IsSuccess).Lease;
        (await second.GetActiveLeaseAsync(_feature)).Should().BeEquivalentTo(lease);
        (await first.HasAnyActiveLeasesAsync()).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task EditOnSecondTask_HonoursLeaseClaimedOnFirstTask_UntilReleased()
    {
        await using var firstPool = NpgsqlDataSource.Create(database.ConnectionString);
        await using var secondPool = NpgsqlDataSource.Create(database.ConnectionString);
        var firstTask = new PostgresFeatureLockService(new JobStoreDatabase(firstPool, _schema));
        var secondTask = new PostgresFeatureLockService(new JobStoreDatabase(secondPool, _schema));
        var secondTaskGuard = new FeatureEditGuard(secondTask);
        var parcel = FeatureRef.Canonical("Parcels", 3, 7L);
        var otherEditor = new LockHolder("other-editor", "Other", "other-session", "tenant", "other-principal");

        (await FeatureEditLockEnforcement.IsEvaluationRequiredAsync(secondTask)).Should().BeFalse();
        (await firstTask.ClaimAsync(parcel, _holder, TimeSpan.FromMinutes(1), _write)).Status
            .Should().Be(FeatureLockClaimStatus.Claimed);

        (await FeatureEditLockEnforcement.IsEvaluationRequiredAsync(secondTask)).Should().BeTrue();
        var conflict = await FeatureEditLockEnforcement.EvaluateAsync(secondTaskGuard, "Parcels", 3, 7L, "update", otherEditor);
        conflict.Should().NotBeNull();
        conflict!.Lock!.Holder.HolderId.Should().Be(_holder.HolderId);
        (await FeatureEditLockEnforcement.EvaluateAsync(secondTaskGuard, "Parcels", 3, 7L, "update", _holder)).Should().BeNull();
        (await FeatureEditLockEnforcement.EvaluateAsync(secondTaskGuard, "Parcels", 3, 8L, "update", otherEditor)).Should().BeNull();
        (await secondTask.ClaimAsync(parcel, otherEditor, TimeSpan.FromMinutes(1), _write)).Status
            .Should().Be(FeatureLockClaimStatus.HeldByOther);

        (await secondTask.ReleaseAsync(parcel, _holder, _write)).Status.Should().Be(FeatureLockReleaseStatus.Released);
        (await FeatureEditLockEnforcement.EvaluateAsync(secondTaskGuard, "Parcels", 3, 7L, "update", otherEditor)).Should().BeNull();
        (await firstTask.ClaimAsync(parcel, otherEditor, TimeSpan.FromMinutes(1), _write)).Status
            .Should().Be(FeatureLockClaimStatus.Claimed);
    }

    [IntegrationTest]
    public async Task RenewAndRelease_RecreatedPool_KeepHolderRulesAndLeaseIdentity()
    {
        FeatureLockLease? lease;
        await using (var pool = NpgsqlDataSource.Create(database.ConnectionString))
        {
            lease = (await new PostgresFeatureLockService(new JobStoreDatabase(pool, _schema))
                .ClaimAsync(_feature, _holder, TimeSpan.FromMinutes(1), _write)).Lease;
        }
        var otherNode = Store();
        var wrongSession = _holder with { SessionId = "other" };
        (await otherNode.RenewAsync(_feature, wrongSession, TimeSpan.FromMinutes(2), _write)).Status
            .Should().Be(FeatureLockRenewStatus.HeldByOther);
        (await otherNode.ReleaseAsync(_feature, wrongSession, _write)).Status.Should().Be(FeatureLockReleaseStatus.HeldByOther);
        var renewal = await otherNode.ClaimAsync(_feature, _holder with { DisplayName = "Updated", TenantId = "other" },
            TimeSpan.FromMinutes(2), _write);
        renewal.Status.Should().Be(FeatureLockClaimStatus.Renewed);
        renewal.Lease!.LockId.Should().Be(lease!.LockId);
        renewal.Lease.AcquiredAt.Should().Be(lease.AcquiredAt);
        renewal.Lease.RenewedAt.Should().NotBeNull();
        (await otherNode.ReleaseAsync(_feature, _holder, _write)).Status.Should().Be(FeatureLockReleaseStatus.Released);
        (await otherNode.GetActiveLeaseAsync(_feature)).Should().BeNull();
        (await otherNode.RenewAsync(_feature, _holder, TimeSpan.FromMinutes(1), _write)).Status.Should().Be(FeatureLockRenewStatus.NotFound);
        (await otherNode.ReleaseAsync(_feature, _holder, _write)).Status.Should().Be(FeatureLockReleaseStatus.NotFound);
    }

    [IntegrationTest]
    public async Task Mutations_DifferentPrincipal_PreserveOriginalLease()
    {
        var store = Store();
        var claimed = await store.ClaimAsync(_feature, _holder, TimeSpan.FromMinutes(1), _write);
        var differentPrincipal = _holder with { PrincipalName = "other-principal" };

        (await Store().ClaimAsync(_feature, differentPrincipal, TimeSpan.FromMinutes(2), _write)).Status
            .Should().Be(FeatureLockClaimStatus.HeldByOther);
        (await Store().RenewAsync(_feature, differentPrincipal, TimeSpan.FromMinutes(2), _write)).Status
            .Should().Be(FeatureLockRenewStatus.HeldByOther);
        (await Store().ReleaseAsync(_feature, differentPrincipal, _write)).Status
            .Should().Be(FeatureLockReleaseStatus.HeldByOther);
        (await store.GetActiveLeaseAsync(_feature)).Should().BeEquivalentTo(claimed.Lease);
        (await store.ReleaseAsync(_feature, _holder, _write)).Status.Should().Be(FeatureLockReleaseStatus.Released);
    }

    [IntegrationTest]
    public async Task ExpiredLease_CanBeReclaimedAndPruned()
    {
        var meter = Store();
        await meter.ClaimAsync(_feature, _holder, TimeSpan.FromMinutes(1), _write);
        await database.ExecuteAsync($"UPDATE \"{_schema}\".feature_locks SET expires_at = clock_timestamp() - interval '1 second'");
        (await meter.GetActiveLeaseAsync(_feature)).Should().BeNull();
        var replacement = await Store().ClaimAsync(_feature, _holder with { HolderId = "new" }, TimeSpan.FromMinutes(1), _write);
        replacement.Status.Should().Be(FeatureLockClaimStatus.Claimed);
        await database.ExecuteAsync($"UPDATE \"{_schema}\".feature_locks SET expires_at = clock_timestamp() - interval '1 second'");
        (await Store().PruneExpiredAsync()).Should().Be(1);
        (await meter.HasAnyActiveLeasesAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task RetentionSweep_DeletesOnlyRequestedExpiredBatch()
    {
        var store = Store();
        await store.ClaimAsync(_feature, _holder, TimeSpan.FromMinutes(1), _write);
        await store.ClaimAsync(_feature with { FeatureId = "002" }, _holder, TimeSpan.FromMinutes(1), _write);
        await database.ExecuteAsync($"UPDATE \"{_schema}\".feature_locks SET expires_at = clock_timestamp() - interval '1 second'");
        var active = _feature with { FeatureId = "003" };
        await store.ClaimAsync(active, _holder, TimeSpan.FromMinutes(1), _write);
        (await store.DeleteExpiredAsync(1, CancellationToken.None)).Should().Be(1);
        (await store.DeleteExpiredAsync(10, CancellationToken.None)).Should().Be(1);
        (await store.DeleteExpiredAsync(10, CancellationToken.None)).Should().Be(0);
        (await store.GetActiveLeaseAsync(active)).Should().NotBeNull();
        (await store.HasAnyActiveLeasesAsync()).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task UnauthorizedMutations_CreateNoLease()
    {
        var meter = Store();
        (await meter.ClaimAsync(_feature, _holder, TimeSpan.FromMinutes(1), FeatureLockAccessContext.ReadOnly)).Status
            .Should().Be(FeatureLockClaimStatus.Denied);
        (await meter.RenewAsync(_feature, _holder, TimeSpan.FromMinutes(1), FeatureLockAccessContext.Unauthorized)).Status
            .Should().Be(FeatureLockRenewStatus.Denied);
        (await meter.ReleaseAsync(_feature, _holder, FeatureLockAccessContext.ReadOnly)).Status.Should().Be(FeatureLockReleaseStatus.Denied);
        (await meter.HasAnyActiveLeasesAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task RenewAsync_LeaseWithoutPrincipal_AdmitsTheSameHolderAndKeepsItWithoutPrincipal()
    {
        var unauthenticated = _holder with { PrincipalName = null };
        (await Store().ClaimAsync(_feature, unauthenticated, TimeSpan.FromMinutes(2), _write)).Status
            .Should().Be(FeatureLockClaimStatus.Claimed);

        var renewed = await Store().RenewAsync(_feature, _holder, TimeSpan.FromMinutes(2), _write);

        renewed.Status.Should().Be(FeatureLockRenewStatus.Renewed);
        (await Store().GetActiveLeaseAsync(_feature))!.Holder.PrincipalName.Should().BeNull(
            "a renewal cannot bind a lease without a principal to the renewing principal");
    }

    private PostgresFeatureLockService Store() => new(new JobStoreDatabase(database.DataSource, _schema));
}
