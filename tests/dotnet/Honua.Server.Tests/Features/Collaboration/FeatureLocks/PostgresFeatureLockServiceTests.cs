// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Collaboration.FeatureLocks;
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
            "Honua.Server.Migrations.167_CreateFeatureLocks.sql")
            ?? throw new InvalidOperationException("Feature lock migration is missing.");
        using var reader = new StreamReader(stream);
        await database.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", _schema, StringComparison.Ordinal));
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

    private PostgresFeatureLockService Store() => new(new JobStoreDatabase(database.DataSource, _schema));
}
