// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class ImportScratchStorageTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task Allocate_IndependentConnectionsShareCapacity_UntilHandleClosesAndFileIsDeleted()
    {
        using var disk = new Disk();
        await using var first = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var second = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var budget = Budget(first, disk.Root);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
        var restarted = new ImportScratchStorage(disk.Root, Budget(second, disk.Root));
        var session = Session();
        var lease = await new ImportScratchStorage(disk.Root, budget).AllocateAsync(session, disk.Root, Bytes);
        try
        {
            await lease.Content.WriteAsync(new byte[Bytes]);
            await lease.Content.FlushAsync();
            ((long)await second.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes);
            await FluentActions.Invoking(() => restarted.AllocateAsync(Session(), disk.Root, Bytes))
                .Should().ThrowAsync<InvalidOperationException>();
            Directory.GetFiles(disk.Root).Should().ContainSingle();
        }
        finally
        {
            await lease.DisposeAsync();
        }

        lease.Content.CanRead.Should().BeFalse();
        Directory.GetFiles(disk.Root).Should().BeEmpty();
        ((long)await second.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(0);
        await lease.DisposeAsync();
        await using var replacement = await restarted.AllocateAsync(Session(), disk.Root, Bytes);
        ((long)await first.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes);
    }

    [IntegrationTest]
    public async Task Allocate_ReservationReplyLost_KeepsChargeWithoutAllocatingFile()
    {
        using var disk = new Disk();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var budget = Budget(connection, disk.Root);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
        var interrupted = Substitute.For<IImportStorageBudget>();
        interrupted.TryReserveAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                (await budget.TryReserveAsync(call.ArgAt<ImportStorageReservation>(0))).Should().BeTrue();
                throw new IOException("Reservation response lost");
            });

        await FluentActions.Invoking(() => new ImportScratchStorage(disk.Root, interrupted)
            .AllocateAsync(Session(), disk.Root, Bytes)).Should().ThrowAsync<IOException>();

        Directory.Exists(disk.Root).Should().BeFalse();
        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes);
        (await connection.GetDatabase().KeyTimeToLiveAsync(budget.Key)).Should().BeNull();
        await FluentActions.Invoking(() => new ImportScratchStorage(disk.Root, budget)
            .AllocateAsync(Session(), disk.Root, Bytes)).Should().ThrowAsync<InvalidOperationException>();
    }

    [IntegrationTest]
    public async Task Dispose_DeletionFails_DoesNotReleaseReservation()
    {
        using var disk = new Disk();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var budget = Budget(connection, disk.Root);
        await ImportStorageBudgetTestFactory.InitializeEmptyAsync(budget);
        var lease = await new ImportScratchStorage(disk.Root, budget).AllocateAsync(Session(), disk.Root, Bytes);
        var path = lease.Content.Name;
        // Simulate an unconfirmed cleanup outcome after the writer has closed.
        await lease.Content.DisposeAsync();
        File.Delete(path);
        Directory.CreateDirectory(path);

        await FluentActions.Invoking(async () => await lease.DisposeAsync()).Should().ThrowAsync<Exception>();

        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "bytes")).Should().Be(Bytes);
        ((long)await connection.GetDatabase().HashGetAsync(budget.Key, "count")).Should().Be(1);
    }

    [IntegrationTest]
    public async Task Allocate_DifferentDirectory_DoesNotReserveOrCreateFiles()
    {
        using var disk = new Disk();
        var budget = Substitute.For<IImportStorageBudget>();
        await FluentActions.Invoking(() => new ImportScratchStorage(disk.Root, budget)
            .AllocateAsync(Session(), Path.Join(disk.Root, "different-volume"), Bytes))
            .Should().ThrowAsync<ArgumentException>();
        budget.ReceivedCalls().Should().BeEmpty();
        Directory.Exists(disk.Root).Should().BeFalse();
    }

    private const int Bytes = 65536;
    private static RedisImportStorageBudget Budget(IConnectionMultiplexer connection, string domain) =>
        ImportStorageBudgetTestFactory.Bind(connection, domain, new ImportStorageBudgetLimits(Bytes, Bytes, Bytes, 10));

    private static ImportUploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Scope = new("owner", "tenant", "target", "workspace", "task", 1),
        IntentSha256 = new string('a', 64), FileName = "parcels.gpkg", TotalBytes = Bytes,
        ChunkBytes = Bytes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private sealed class Disk : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-scratch-budget-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
