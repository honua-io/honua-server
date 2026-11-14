// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Attachments;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Collection("Database")]
public sealed partial class AttachmentUploadSessionStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task AppendAsync_InvalidDigestAndOversizedChunk_DoesNotAdvance()
    {
        await WithStoreAsync(async (store, schema) =>
        {
            var session = await store.BeginAsync(CreateRequest());
            var key = new AttachmentUploadSessionKey("owner", session.SessionId);
            var invalid = new AttachmentUploadChunk { Key = key, OffsetBytes = 0, Content = "abc"u8.ToArray(), Sha256 = SHA256.HashData("abd"u8) };
            (await Assert.ThrowsAsync<AttachmentUploadException>(() => store.AppendAsync(invalid)))
                .Failure.Should().Be(AttachmentUploadFailure.InvalidContent);
            var oversized = new AttachmentUploadChunk { Key = key, OffsetBytes = 0, Content = "abcd"u8.ToArray(), Sha256 = SHA256.HashData("abcd"u8) };
            (await Assert.ThrowsAsync<AttachmentUploadException>(() => store.AppendAsync(oversized)))
                .Failure.Should().Be(AttachmentUploadFailure.InvalidContent);
            (await CreateStore(schema).GetAsync(key)).NextOffsetBytes.Should().Be(0);
        });
    }

    [IntegrationTest]
    public async Task BeginAsync_IdenticalAndConflictingRetries_PreservesCallerScopedIdentity()
    {
        await WithStoreAsync(async (store, schema) =>
        {
            var request = CreateRequest();
            var first = await store.BeginAsync(request);
            var restarted = CreateStore(schema);
            (await restarted.BeginAsync(request)).SessionId.Should().Be(first.SessionId);
            var conflict = await Assert.ThrowsAsync<AttachmentUploadException>(() =>
                restarted.BeginAsync(request with { Filename = "different.txt" }));
            conflict.Failure.Should().Be(AttachmentUploadFailure.Conflict);
            (await restarted.BeginAsync(request with { OwnerId = "other-owner" })).SessionId.Should().NotBe(first.SessionId);
            var hidden = await Assert.ThrowsAsync<AttachmentUploadException>(() => restarted.GetAsync(
                new AttachmentUploadSessionKey("other-owner", first.SessionId)));
            hidden.Failure.Should().Be(AttachmentUploadFailure.NotFound);
        });
    }

    [IntegrationTest]
    public async Task AppendAsync_ConcurrentExactRetriesAndConflictingBytes_AdvancesOnce()
    {
        await WithStoreAsync(async (store, schema) =>
        {
            var session = await store.BeginAsync(CreateRequest());
            var key = new AttachmentUploadSessionKey("owner", session.SessionId);
            var content = "abc"u8.ToArray();
            var append = new AttachmentUploadChunk { Key = key, OffsetBytes = 0, Content = content, Sha256 = SHA256.HashData(content) };
            var results = await Task.WhenAll(store.AppendAsync(append), CreateStore(schema).AppendAsync(append));
            results.Should().OnlyContain(result => result.NextOffsetBytes == 3);
            var conflict = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.AppendAsync(
                append with { Content = "abd"u8.ToArray(), Sha256 = SHA256.HashData("abd"u8) }));
            conflict.Failure.Should().Be(AttachmentUploadFailure.InvalidState);
            var gap = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.AppendAsync(append with { OffsetBytes = 4 }));
            gap.Failure.Should().Be(AttachmentUploadFailure.InvalidState);
            (await CreateStore(schema).GetAsync(key)).NextOffsetBytes.Should().Be(3);
        });
    }

    [IntegrationTest]
    public async Task AbortAsync_RepeatedAbortAndExpiredSession_CannotResume()
    {
        await WithStoreAsync(async (store, schema) =>
        {
            var session = await store.BeginAsync(CreateRequest());
            var key = new AttachmentUploadSessionKey("owner", session.SessionId);
            await store.AbortAsync(key);
            await CreateStore(schema).AbortAsync(key);
            var append = new AttachmentUploadChunk { Key = key, OffsetBytes = 0, Content = "abc"u8.ToArray(), Sha256 = SHA256.HashData("abc"u8) };
            (await Assert.ThrowsAsync<AttachmentUploadException>(() => store.AppendAsync(append)))
                .Failure.Should().Be(AttachmentUploadFailure.InvalidState);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                UPDATE "{schema}".attachment_upload_sessions
                SET created_at = now() - interval '2 hours', expires_at = now() - interval '1 hour'
                """, connection);
            await command.ExecuteNonQueryAsync();
            (await Assert.ThrowsAsync<AttachmentUploadException>(() => store.GetAsync(key)))
                .Failure.Should().Be(AttachmentUploadFailure.Expired);
        });
    }

    private static AttachmentUploadRequest CreateRequest() => new()
    {
        OwnerId = "owner",
        ServiceId = "service",
        LayerId = 0,
        FeatureId = 42,
        Operation = "chunked",
        IdempotencyKey = "retry",
        Filename = "notes.txt",
        ContentType = "text/plain",
        Keywords = [],
        ExpectedSize = 6,
        Sha256 = SHA256.HashData("abcdef"u8),
        MaxChunkBytes = 3,
        Lifetime = TimeSpan.FromHours(1)
    };

    private PostgresAttachmentUploadSessionStore CreateStore(string schema)
        => new(new FixtureConnectionProvider(fixture), schema);

    private async Task WithStoreAsync(Func<PostgresAttachmentUploadSessionStore, string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentUploadSessionStoreTests));
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
            {
                root = root.Parent;
            }
            var sql = await File.ReadAllTextAsync(Path.Join(root!.FullName,
                "src", "Honua.Server", "Migrations", "162_CreateAttachmentUploadSessions.sql"));
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal), connection);
            await command.ExecuteNonQueryAsync();
            await action(CreateStore(schema), schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed class FixtureConnectionProvider(PostgresFixture fixture) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => fixture.ConnectionString;
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await fixture.DataSource.OpenConnectionAsync(cancellationToken);
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead, CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
