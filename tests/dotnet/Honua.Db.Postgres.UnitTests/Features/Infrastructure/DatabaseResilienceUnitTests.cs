// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Db.Postgres.Features.AuditLog;
using Honua.Db.Postgres.Features.Infrastructure.Events.Outbox;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Db.Postgres.UnitTests.Features.Infrastructure;

[Protocol(TestProtocols.TestQuality)]
public sealed class DatabaseResilienceUnitTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AuditPruner_BuildLockTimeoutSql_UsesTightLocalLockTimeout()
    {
        PostgresAuditLogRetentionPruner.BuildLockTimeoutSql(PostgresAuditLogRetentionPruner.DefaultLockTimeout)
            .Should().Be("SET LOCAL lock_timeout = '2s'");
        PostgresAuditLogRetentionPruner.BuildLockTimeoutSql(TimeSpan.FromMilliseconds(250))
            .Should().Be("SET LOCAL lock_timeout = '250ms'");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AuditPruner_ExecuteWithLockBackoffAsync_Retries55P03WithExponentialBackoffAndRecovers()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var options = new PostgresAuditLogRetentionPruner.LockBackoffOptions
        {
            LockTimeout = TimeSpan.FromSeconds(2),
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMilliseconds(100),
            DelayAsync = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        };

        var deleted = await PostgresAuditLogRetentionPruner.ExecuteWithLockBackoffAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new PostgresException("lock timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable);
                }

                return Task.FromResult(42);
            },
            options,
            CancellationToken.None);

        deleted.Should().Be(42);
        attempts.Should().Be(3);
        delays.Should().Equal(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task AuditPruner_ExecuteWithLockBackoffAsync_Persistent55P03_DefersGracefullyWithoutThrowing()
    {
        var delays = new List<TimeSpan>();
        var options = new PostgresAuditLogRetentionPruner.LockBackoffOptions
        {
            LockTimeout = TimeSpan.FromSeconds(2),
            MaxRetries = 2,
            BaseDelay = TimeSpan.FromMilliseconds(50),
            DelayAsync = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        };

        var deleted = await PostgresAuditLogRetentionPruner.ExecuteWithLockBackoffAsync(
            _ => throw new PostgresException("lock timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable),
            options,
            CancellationToken.None);

        deleted.Should().BeNull("persistent lock contention should defer remaining batches instead of failing");
        delays.Should().Equal(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void FeatureChangeOutbox_BacklogMetricsSql_FiltersOutDispatchedRows()
    {
        var sql = PostgresFeatureChangeOutboxRepository.BacklogMetricsSql;

        sql.Should().Contain("WHERE status <> 'dispatched'");
        sql.Should().Contain("AS pending_count");
        sql.Should().Contain("AS dead_lettered_count");
        sql.Should().Contain("AS oldest_pending_seconds");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AttachmentUploadSessionStore_IsTerminalCommitFailure_DistinguishesTerminalFromRetryable()
    {
        PostgresAttachmentUploadSessionStore.IsTerminalCommitFailure(
            new AttachmentUploadException(AttachmentUploadFailure.NotFound, "Parent deleted"))
            .Should().BeTrue();
        PostgresAttachmentUploadSessionStore.IsTerminalCommitFailure(
            new AttachmentUploadException(AttachmentUploadFailure.Expired, "Session expired"))
            .Should().BeTrue();
        PostgresAttachmentUploadSessionStore.IsTerminalCommitFailure(
            new ValidationException("Quota exceeded"))
            .Should().BeFalse("recoverable quota errors leave the session uploading for retry");
        PostgresAttachmentUploadSessionStore.IsTerminalCommitFailure(
            new PostgresException("check violation", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation))
            .Should().BeFalse();
    }
}
