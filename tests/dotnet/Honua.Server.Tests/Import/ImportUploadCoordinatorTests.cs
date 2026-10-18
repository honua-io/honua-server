// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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
[Operation(Operations.Import)]
public sealed class ImportUploadCoordinatorTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task Create_LostCommitReply_OriginalTaskRecoversSameSessionAndDeadlineAfterRestart()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => connection.GetDatabase().HashGetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<RedisValue>(1)));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0),
                    call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Lost create receipt after actual commit");
            });
        var interruptedConnection = Substitute.For<IConnectionMultiplexer>();
        interruptedConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var interrupted = new ImportUploadCoordinator(fixture.Tasks,
            new RedisImportUploadStore(interruptedConnection), fixture.Storage);

        await FluentActions.Invoking(() => interrupted.CreateAsync(fixture.Actor, fixture.Reference))
            .Should().ThrowAsync<RedisConnectionException>();

        await using var restart = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var recovered = new ImportUploadCoordinator(fixture.Tasks, new RedisImportUploadStore(restart),
            fixture.Storage);
        var original = (await recovered.GetAsync(fixture.Actor, fixture.Reference))!;
        original.ExpiresAt.Should().Be(fixture.Task.UploadAdmissionDeadline);
        var expiry = await restart.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(original.Id));
        expiry.Should().NotBeNull();
        (await recovered.CreateAsync(fixture.Actor, fixture.Reference)).Should().Be(original);
        (await restart.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(original.Id))).Should().Be(expiry);
        original.Version.Should().Be(1);
    }

    [IntegrationTest]
    public async Task Append_AuthorityDeniedBeforeRead_DoesNotConsumeBodyOrCreateChunk()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        fixture.Allowed = false;
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);

        await FluentActions.Invoking(() => fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, input, Fixture.Digest)).Should().ThrowAsync<UnauthorizedAccessException>();

        input.Position.Should().Be(0);
        (await fixture.Storage.ListFilesAsync()).Should().BeEmpty();
        (await fixture.Store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    [IntegrationTest]
    public async Task Append_AuthorityRevokedAfterStorage_DoesNotAcceptCandidateBytes()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        fixture.MaximumAuthorizedCalls = fixture.AuthorizationCalls + 1;
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);

        await FluentActions.Invoking(() => fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, input, Fixture.Digest)).Should().ThrowAsync<UnauthorizedAccessException>();

        input.Position.Should().Be(Fixture.Bytes.Length);
        (await fixture.Store.GetAsync(session.Id, session.Scope)).Should().Be(session);
        (await fixture.Store.ReadChunksAsync(session.Id, session.Scope, 0, 1)).Should().BeEmpty();
        // An unaccepted candidate may remain for retention cleanup; it is not a source receipt.
        (await fixture.Storage.ListFilesAsync()).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task Verify_AuthorityRevokedDuringProviderRead_DoesNotSeal()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);
        var complete = await fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference, session.Version, 0, input, Fixture.Digest);
        var observed = Substitute.For<ICloudFileStorage>();
        observed.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => fixture.Storage.GetMetadataAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        observed.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var content = await fixture.Storage.DownloadAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
                fixture.Allowed = false;
                return content;
            });
        var coordinator = new ImportUploadCoordinator(fixture.Tasks, fixture.Store, observed);

        await FluentActions.Invoking(() => coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        (await fixture.Store.GetAsync(complete.Id, complete.Scope)).Should().Be(complete);
        complete.State.Should().Be(ImportUploadState.Receiving);
    }

    [IntegrationTest]
    public async Task CompleteTransfer_ReplaysSealAsObservation_AndRejectsStaleRangeBeforeBodyRead()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);
        var complete = await fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference, session.Version, 0, input, Fixture.Digest);
        await using var retry = new MemoryStream(Fixture.Bytes, writable: false);
        await FluentActions.Invoking(() => fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, retry, Fixture.Digest)).Should().ThrowAsync<InvalidOperationException>();
        retry.Position.Should().Be(0);
        var sealedSession = await fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version);

        sealedSession.State.Should().Be(ImportUploadState.Sealed);
        sealedSession.ContentSha256.Should().Be(Fixture.Digest);
        (await fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version)).Should().Be(sealedSession);
        (await fixture.Coordinator.GetAsync(fixture.Actor, fixture.Reference)).Should().Be(sealedSession);
        (await fixture.Storage.ListFilesAsync()).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task ExpiredOriginalTask_CannotCreateNewSessionOrExtendDeadline()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        fixture.Task = fixture.Task with { UploadAdmissionDeadline = DateTimeOffset.UtcNow.AddMinutes(-1) };

        (await fixture.Coordinator.GetAsync(fixture.Actor, fixture.Reference)).Should().BeNull();
        await FluentActions.Invoking(() => fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference))
            .Should().ThrowAsync<InvalidOperationException>();
        (await fixture.Coordinator.GetAsync(fixture.Actor, fixture.Reference)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task MissingExpectedWholeFileDigest_CannotStartResumableUpload()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        fixture.Task = fixture.Task with { Source = fixture.Task.Source with { ExpectedSha256 = null } };

        await FluentActions.Invoking(() => fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference))
            .Should().ThrowAsync<ArgumentException>();
        (await fixture.Storage.ListFilesAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Cancel_ForeignActorCannotObserve_OriginalCancellationPreventsLateAppend()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        var foreign = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "foreign")], "test"));
        await FluentActions.Invoking(() => fixture.Coordinator.GetAsync(foreign, fixture.Reference))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        var cancelled = await fixture.Coordinator.CancelAsync(fixture.Actor, fixture.Reference, session.Version);
        cancelled.State.Should().Be(ImportUploadState.Cancelled);
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);
        await FluentActions.Invoking(() => fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, input, Fixture.Digest)).Should().ThrowAsync<InvalidOperationException>();
        input.Position.Should().Be(0);
    }

    [IntegrationTest]
    public async Task SealedReceipt_WriteAdmissionDenied_RecoveryDoesNotMutateOrReopenUpload()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);
        var complete = await fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, input, Fixture.Digest);
        var sealedSession = await fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version);
        var expiry = await connection.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id));
        fixture.WriteAllowed = false; // For example, successful import now occupies the destination.

        (await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference)).Should().Be(sealedSession);
        (await fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version)).Should().Be(sealedSession);
        (await fixture.Coordinator.GetAsync(fixture.Actor, fixture.Reference)).Should().Be(sealedSession);
        (await connection.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id))).Should().Be(expiry);
        (await fixture.Storage.ListFilesAsync()).Should().ContainSingle();

        fixture.Allowed = false;
        await FluentActions.Invoking(() => fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [IntegrationTest]
    public async Task CancelledReceipt_WriteAdmissionDenied_RecoveryPreservesOriginalCancellation()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        var cancelled = await fixture.Coordinator.CancelAsync(fixture.Actor, fixture.Reference, session.Version);
        fixture.WriteAllowed = false;

        (await fixture.Coordinator.CancelAsync(fixture.Actor, fixture.Reference, session.Version)).Should().Be(cancelled);
        (await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference)).Should().Be(cancelled);
        (await fixture.Store.GetAsync(session.Id, session.Scope)).Should().Be(cancelled);
        fixture.Allowed = false;
        await FluentActions.Invoking(() => fixture.Coordinator.CancelAsync(fixture.Actor, fixture.Reference, session.Version))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [IntegrationTest]
    public async Task ReadOnlyAuthority_CannotAdmitAppendSealOrCancelAnUpload()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        fixture.WriteAllowed = false;
        await FluentActions.Invoking(() => fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        (await fixture.Coordinator.GetAsync(fixture.Actor, fixture.Reference)).Should().BeNull();
        fixture.WriteAllowed = true;
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        fixture.WriteAllowed = false;
        await using var rejected = new MemoryStream(Fixture.Bytes, writable: false);
        await FluentActions.Invoking(() => fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, rejected, Fixture.Digest)).Should().ThrowAsync<UnauthorizedAccessException>();
        rejected.Position.Should().Be(0);
        await FluentActions.Invoking(() => fixture.Coordinator.CancelAsync(fixture.Actor, fixture.Reference, session.Version))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        fixture.WriteAllowed = true;
        await using var input = new MemoryStream(Fixture.Bytes, writable: false);
        var complete = await fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, input, Fixture.Digest);
        fixture.WriteAllowed = false;

        await FluentActions.Invoking(() => fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        (await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference)).Should().Be(complete);
        (await fixture.Store.GetAsync(session.Id, session.Scope)).Should().Be(complete);
    }

    [IntegrationTest]
    public async Task Retain_LostCommitReply_RecoversOriginalSourceWithReadAccessWithoutWritingAgain()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await SealAsync(fixture);
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => connection.GetDatabase().HashGetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<RedisValue>(1)));
        database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(call => connection.GetDatabase().HashGetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<RedisValue[]>(1)));
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                await connection.GetDatabase().ScriptEvaluateAsync(call.ArgAt<string>(0),
                    call.ArgAt<RedisKey[]>(1), call.ArgAt<RedisValue[]>(2));
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Lost source receipt after actual bind");
            });
        var interruptedConnection = Substitute.For<IConnectionMultiplexer>();
        interruptedConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var interrupted = new ImportUploadCoordinator(fixture.Tasks, new RedisImportUploadStore(interruptedConnection), fixture.Storage);
        await FluentActions.Invoking(() => interrupted.RetainSourceAsync(fixture.Actor, fixture.Reference,
            session.Version, Path.Join(fixture.Root, "scratch"), session.TotalBytes)).Should().ThrowAsync<RedisConnectionException>();
        var original = (await fixture.Store.GetAsync(session.Id, session.Scope))!;
        original.StagedSource.Should().NotBeNull();
        var expiry = await connection.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id));

        fixture.WriteAllowed = false;
        await using var restart = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var inaccessible = Substitute.For<ICloudFileStorage>();
        var recovered = new ImportUploadCoordinator(fixture.Tasks, new RedisImportUploadStore(restart), inaccessible);
        var receipt = await recovered.RetainSourceAsync(fixture.Actor, fixture.Reference, session.Version,
            Path.Join(fixture.Root, "scratch"), session.TotalBytes);

        receipt.Should().Be(original);
        inaccessible.ReceivedCalls().Should().BeEmpty();
        (await restart.GetDatabase().KeyExpireTimeAsync(RedisImportUploadStore.Key(session.Id))).Should().Be(expiry);
        fixture.Allowed = false;
        await FluentActions.Invoking(() => recovered.RetainSourceAsync(fixture.Actor, fixture.Reference,
            session.Version, Path.Join(fixture.Root, "scratch"), session.TotalBytes)).Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [IntegrationTest]
    public async Task Retain_NoWriteAuthority_DoesNotReadSourceOrAllocateScratch()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await SealAsync(fixture);
        fixture.WriteAllowed = false;
        var inaccessible = Substitute.For<ICloudFileStorage>();
        var coordinator = new ImportUploadCoordinator(fixture.Tasks, fixture.Store, inaccessible);
        var scratch = Path.Join(fixture.Root, "scratch");

        await FluentActions.Invoking(() => coordinator.RetainSourceAsync(fixture.Actor, fixture.Reference,
            session.Version, scratch, session.TotalBytes)).Should().ThrowAsync<UnauthorizedAccessException>();

        inaccessible.ReceivedCalls().Should().BeEmpty();
        Directory.Exists(scratch).Should().BeFalse();
        (await fixture.Store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    private static async Task<ImportUploadSession> SealAsync(Fixture fixture)
    {
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        await using var content = new MemoryStream(Fixture.Bytes, writable: false);
        var complete = await fixture.Coordinator.AppendAsync(fixture.Actor, fixture.Reference,
            session.Version, 0, content, Fixture.Digest);
        return await fixture.Coordinator.VerifyAndSealAsync(fixture.Actor, fixture.Reference, complete.Version);
    }

    [IntegrationTest]
    public async Task Get_ReadAuthorityRevokedDuringReceiptLookup_DoesNotReturnReceipt()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var fixture = new Fixture(connection);
        var session = await fixture.Coordinator.CreateAsync(fixture.Actor, fixture.Reference);
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(async call =>
            {
                var stored = await connection.GetDatabase().HashGetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<RedisValue>(1));
                stored.HasValue.Should().BeTrue();
                fixture.Allowed = false;
                return stored;
            });
        var observed = Substitute.For<IConnectionMultiplexer>();
        observed.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var coordinator = new ImportUploadCoordinator(fixture.Tasks, new RedisImportUploadStore(observed), fixture.Storage);

        await FluentActions.Invoking(() => coordinator.GetAsync(fixture.Actor, fixture.Reference))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        (await fixture.Store.GetAsync(session.Id, session.Scope)).Should().Be(session);
    }

    private sealed class Fixture : IDisposable
    {
        public static byte[] Bytes { get; } = "small actual GIS input"u8.ToArray();
        public static string Digest { get; } = Convert.ToHexStringLower(SHA256.HashData(Bytes));
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-upload-coordinator-" + Guid.NewGuid().ToString("N"));
        public ClaimsPrincipal Actor { get; } = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
        public IDatasetImportTaskProvider Tasks { get; } = Substitute.For<IDatasetImportTaskProvider>();
        public bool Allowed { get; set; } = true;
        public bool WriteAllowed { get; set; } = true;
        public int AuthorizationCalls { get; private set; }
        public int MaximumAuthorizedCalls { get; set; } = int.MaxValue;
        public DatasetImportTask Task { get; set; } = new(Guid.NewGuid().ToString("N"), 1, new string('a', 64),
            "owner", "tenant", "target", "workspace", new("honua_data", "new_table"),
            new("browser-file", "data.geojson", Bytes.Length, Digest), new(), DateTimeOffset.UtcNow.AddHours(1));
        public ImportUploadTaskReference Reference => new(Task.WorkspaceId, Task.TaskId, Task.Revision, Task.ContentHash);
        public LocalFileStorage Storage { get; }
        public RedisImportUploadStore Store { get; }
        public ImportUploadCoordinator Coordinator { get; }

        public Fixture(IConnectionMultiplexer connection)
        {
            Storage = new LocalFileStorage(Options.Create(new LocalStorageOptions { BasePath = Root, CreateDirectoryIfNotExists = true }),
                NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());
            Store = new RedisImportUploadStore(connection);
            Tasks.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(),
                    Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    AuthorizationCalls++;
                    var principal = call.ArgAt<ClaimsPrincipal>(0);
                    return System.Threading.Tasks.Task.FromResult<DatasetImportTask?>(Allowed && AuthorizationCalls <= MaximumAuthorizedCalls &&
                        (!call.ArgAt<bool>(5) || WriteAllowed) &&
                        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value == Task.OwnerActorId &&
                        call.ArgAt<string>(1) == Task.WorkspaceId && call.ArgAt<string>(2) == Task.TaskId &&
                        call.ArgAt<long>(3) == Task.Revision && call.ArgAt<string>(4) == Task.ContentHash ? Task : null);
                });
            Coordinator = new ImportUploadCoordinator(Tasks, Store, Storage);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
