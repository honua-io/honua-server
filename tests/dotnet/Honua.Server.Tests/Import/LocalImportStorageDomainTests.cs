// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class LocalImportStorageDomainTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task DistinctObjectsCanNestEvenWhenTheirHashesCollideInALockStripe()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var domain = disk.Domain();
        await domain.InitializeAsync();
        var session = Session();
        var firstKey = $"imports/resumable/{session.Id}/{Guid.NewGuid():N}.part";
        var stripe = SHA256.HashData(Encoding.UTF8.GetBytes(firstKey))[0] % 64;
        var secondKey = Enumerable.Range(0, 16384)
            .Select(index => $"imports/resumable/{session.Id}/" + index.ToString("x32", CultureInfo.InvariantCulture) + ".part")
            .First(key => key != firstKey && SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] % 64 == stripe);
        var first = RedisImportStorageBudget.ForObject(session, firstKey, 16);
        var second = RedisImportStorageBudget.ForObject(session, secondKey, 16);
        Assert.True(await domain.TryReserveAsync(first));
        await using var firstWriter = await domain.EnterObjectMutationAsync(firstKey, 16);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await domain.TryReserveAsync(second, deadline.Token));
        await using var secondWriter = await domain.EnterObjectMutationAsync(secondKey, 16, deadline.Token);
        Assert.NotEqual((await disk.Journal().ReadEntryAsync(firstKey))!.Slot,
            (await disk.Journal().ReadEntryAsync(secondKey))!.Slot);
    }

    [IntegrationTest]
    public async Task ActualProviderObjectSurvivesRedisLossAndRejectsOldEpochRelease()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var domain = disk.Domain();
        await Assert.ThrowsAsync<InvalidOperationException>(() => domain.EnterMutationAsync().AsTask());
        await domain.InitializeAsync();
        var storage = disk.Storage(domain);
        var session = Session();
        var chunk = await StageAsync(storage, domain, session);
        var reservation = RedisImportStorageBudget.ForObject(session, chunk.FileId, chunk.Bytes);
        var original = await disk.LedgerAsync();
        Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(original.Key, "bytes"));
        await connection.GetDatabase().KeyDeleteAsync(original.Key);
        await Assert.ThrowsAsync<InvalidDataException>(() => domain.EnterMutationAsync().AsTask());
        var recovered = disk.Domain();
        await recovered.InitializeAsync();
        Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(original.Key, "bytes"));
        Assert.NotEqual(original.Epoch, (await disk.LedgerAsync()).Epoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => original.ReleaseDeletedAsync(reservation));
        Assert.Equal(16, new FileInfo(Path.Join(disk.Roots[ImportStorageRootKind.ProviderObjects], chunk.FileId)).Length);
        // An already initialized replica adopts the verified epoch under a shared
        // physical fence, then performs the real provider's conditional deletion.
        Assert.True(await new ImportUploadChunkStorage(storage, domain).DeleteUnacceptedAsync(session, chunk));
        Assert.Equal(0, (long)await connection.GetDatabase().HashGetAsync(original.Key, "bytes"));
        Assert.Equal(LocalImportStorageState.Retired, (await disk.Journal().ReadEntryAsync(chunk.FileId))!.State);
    }

    [IntegrationTest]
    public async Task FailedRawCensusPoisonsPreviouslyReadyReplicasUntilVerifiedRecovery()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var original = disk.Domain();
        await original.InitializeAsync();
        var oldLedger = await disk.LedgerAsync();
        var orphan = Path.Join(disk.Roots[ImportStorageRootKind.ProviderObjects], "unaccounted");
        await File.WriteAllBytesAsync(orphan, [1]);
        var recovering = disk.Domain();
        await Assert.ThrowsAsync<InvalidDataException>(() => recovering.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => original.EnterMutationAsync().AsTask());
        Assert.Equal(oldLedger.Epoch, await oldLedger.ReadStoredEpochAsync());
        Assert.NotEqual(oldLedger.Epoch, await disk.Journal().ReadEpochAsync());
        File.Delete(orphan);
        await recovering.InitializeAsync();
        await using var lease = await original.EnterMutationAsync();
        Assert.Equal(await disk.Journal().ReadEpochAsync(), await oldLedger.ReadStoredEpochAsync());
    }

    [IntegrationTest]
    public async Task LowerQuotaPreservesExistingPhysicalObjectsAndRefusesNewCapacity()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var original = disk.Domain();
        await original.InitializeAsync();
        var storage = disk.Storage(original);
        _ = await StageAsync(storage, original, Session());
        _ = await StageAsync(storage, original, Session());
        var reduced = disk.Domain(limits: new(16, 16, 16, 1));
        await reduced.InitializeAsync();
        var session = Session();
        var candidate = RedisImportStorageBudget.ForObject(session, $"imports/resumable/{session.Id}/{Guid.NewGuid():N}.part", 16);
        Assert.False(await reduced.TryReserveAsync(candidate));
        var ledger = await disk.LedgerAsync();
        Assert.Equal(32, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
        Assert.Equal(2, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "count"));
        Assert.Equal(2, (await storage.ListFilesAsync()).Count);
        var otherCandidate = RedisImportStorageBudget.ForObject(session, $"imports/resumable/{session.Id}/{Guid.NewGuid():N}.part", 16);
        await Assert.ThrowsAsync<InvalidOperationException>(() => original.TryReserveAsync(otherCandidate));
    }

    [IntegrationTest]
    public async Task RecoveryWaitsForTheEntirePhysicalWriterLifetime()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var domain = disk.Domain();
        await domain.InitializeAsync();
        var session = Session();
        var reservation = RedisImportStorageBudget.ForObject(session, $"imports/resumable/{session.Id}/{Guid.NewGuid():N}.part", 16);
        Assert.True(await domain.TryReserveAsync(reservation));
        var originalEpoch = await disk.Journal().ReadEpochAsync();
        await using (var writer = await domain.EnterObjectMutationAsync(reservation.FileId, 16))
        {
            var path = Path.Join(disk.Roots[ImportStorageRootKind.ProviderObjects], reservation.FileId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, new byte[9]);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.Domain().InitializeAsync(cancellation.Token));
            Assert.Equal(originalEpoch, await disk.Journal().ReadEpochAsync());
        }
        await disk.Domain().InitializeAsync();
        var ledger = await disk.LedgerAsync();
        Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
        Assert.NotEqual(originalEpoch, ledger.Epoch);
    }

    [IntegrationTest]
    public async Task LostReservationReplyLeavesDurableIntentAndRecoveryReclaimsOnlyProvenAbsence()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var armed = false;
        var interrupted = RelayWithLostReply(connection, values => armed && values?.Length == 11);
        var domain = disk.Domain(interrupted);
        await domain.InitializeAsync();
        var session = Session();
        var reservation = RedisImportStorageBudget.ForObject(session, $"imports/resumable/{session.Id}/{Guid.NewGuid():N}.part", 16);
        armed = true;
        await Assert.ThrowsAsync<RedisConnectionException>(() => domain.TryReserveAsync(reservation));
        Assert.Equal(reservation, (await disk.Journal().ReadEntryAsync(reservation.FileId))!.Reservation);
        var ledger = await disk.LedgerAsync();
        Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
        await disk.Domain().InitializeAsync();
        Assert.Equal(0, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
        Assert.Equal(LocalImportStorageState.Retired, (await disk.Journal().ReadEntryAsync(reservation.FileId))!.State);
    }

    [IntegrationTest]
    public async Task LostReleaseReplyCanFinishJournalRetirementWithoutSubtractingOtherObjects()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var armed = false;
        var interrupted = RelayWithLostReply(connection, values => armed && values?.Length == 6);
        var domain = disk.Domain(interrupted);
        await domain.InitializeAsync();
        var storage = disk.Storage(domain);
        var session = Session();
        var first = await StageAsync(storage, domain, session);
        _ = await StageAsync(storage, domain, Session());
        Assert.True(await storage.DeleteIfMatchAsync(first.FileId, first.ETag));
        var reservation = RedisImportStorageBudget.ForObject(session, first.FileId, first.Bytes);
        armed = true;
        await Assert.ThrowsAsync<RedisConnectionException>(() => domain.ReleaseDeletedAsync(reservation));
        Assert.Equal(LocalImportStorageState.Deleting, (await disk.Journal().ReadEntryAsync(first.FileId))!.State);
        armed = false;
        Assert.True(await domain.ReleaseDeletedAsync(reservation));
        Assert.False(await domain.ReleaseDeletedAsync(reservation));
        var ledger = await disk.LedgerAsync();
        Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
        Assert.Equal(1, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "count"));
    }

    [IntegrationTest]
    public async Task SourceAndProcessingScratchCloseObjectLocksBeforeAccountingCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var disk = new Disk(connection);
        var domain = disk.Domain();
        await domain.InitializeAsync();
        var session = Session();
        var source = new ImportScratchStorage(disk.Roots[ImportStorageRootKind.SourceScratch], domain, domain);
        await using var lease = await source.AllocateAsync(session, disk.Roots[ImportStorageRootKind.SourceScratch], 16);
        await lease.Content.WriteAsync(new byte[16]);
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var transfer = new ImportTransferProvenance(session.Id, session.Scope.ActorId, session.Scope.TenantId,
            session.Scope.TargetId, session.Scope.WorkspaceId, session.Scope.TaskId, session.Scope.TaskRevision, session.IntentSha256);
        await using var scratch = await new ImportProcessingScratchFactory(disk.Roots[ImportStorageRootKind.ProcessingScratch], domain, domain)
            .CreateAsync(transfer, 1024 * 1024, 16);
        using var input = new MemoryStream([1, 2, 3]);
        await scratch.WriteFileAsync(input, Path.Join(scratch.DirectoryPath, "shape", "roads.dbf"), 3);
        await scratch.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var ledger = await disk.LedgerAsync();
        Assert.Equal(0, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
        await disk.Domain().InitializeAsync();
    }

    private static async Task<ImportUploadChunk> StageAsync(LocalFileStorage storage, LocalImportStorageDomain domain, ImportUploadSession session)
    {
        var content = new byte[16];
        using var input = new MemoryStream(content);
        return await new ImportUploadChunkStorage(storage, domain).StageAsync(session, input,
            Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static IConnectionMultiplexer RelayWithLostReply(ConnectionMultiplexer connection, Func<RedisValue[]?, bool> loseReply)
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                var values = call.ArgAt<RedisValue[]>(2);
                var result = await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1), values);
                if (loseReply(values)) throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Reply lost after real Redis commit");
                return result;
            });
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => connection.GetDatabase().KeyDeleteAsync(call.ArgAt<RedisKey>(0)));
        var interrupted = Substitute.For<IConnectionMultiplexer>();
        interrupted.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        return interrupted;
    }

    private static ImportUploadSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Scope = new("actor", "tenant", "target", "workspace", "task", 1),
        IntentSha256 = new string('a', 64),
        FileName = "source.geojson",
        TotalBytes = 16,
        ChunkBytes = 65536,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private sealed class Disk : IAsyncDisposable
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly string _directory = Path.Join(Path.GetTempPath(), "honua-domain-" + Guid.NewGuid().ToString("N"));
        private readonly string _domainId = Guid.NewGuid().ToString("N");
        private readonly ImportStorageBudgetLimits _limits = new(4 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 16);
        internal Dictionary<ImportStorageRootKind, string> Roots { get; } = [];
        private string JournalPath => Path.Join(_directory, "journal");
        internal Disk(IConnectionMultiplexer connection)
        {
            _connection = connection;
            Directory.CreateDirectory(_directory);
            foreach (var kind in Enum.GetValues<ImportStorageRootKind>())
            {
                var path = Path.Join(_directory, kind.ToString());
                Directory.CreateDirectory(path);
                Roots.Add(kind, path);
            }
        }
        internal LocalImportStorageDomain Domain(IConnectionMultiplexer? connection = null, ImportStorageBudgetLimits? limits = null) => new(connection ?? _connection,
            _domainId, limits ?? _limits, JournalPath, Roots, _limits.MaximumObjects);
        internal LocalImportStorageJournal Journal() => new(JournalPath, _domainId, _limits.MaximumObjects);
        internal async Task<RedisImportStorageBudget> LedgerAsync() => new(_connection, _domainId, _limits, await Journal().ReadEpochAsync());
        internal LocalFileStorage Storage(LocalImportStorageDomain domain) => new(Options.Create(new LocalStorageOptions
        { BasePath = Roots[ImportStorageRootKind.ProviderObjects], CreateDirectoryIfNotExists = true }),
            NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>(), domain);
        public async ValueTask DisposeAsync()
        {
            await _connection.GetDatabase().KeyDeleteAsync(new RedisImportStorageBudget(_connection, _domainId, _limits,
                Guid.NewGuid().ToString("N")).Key);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
