// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class AuditingMigrationBackupHookRecorderTests
{
    [UnitTest]
    public async Task RecordAsync_CachesOutcomeAndWritesAuditEvent()
    {
        var audit = new CapturingAuditLog();
        using var services = new ServiceCollection()
            .AddSingleton<IAuditLog>(audit)
            .BuildServiceProvider();
        var state = new MigrationBackupHookState();
        var recorder = new AuditingMigrationBackupHookRecorder(
            state,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditingMigrationBackupHookRecorder>.Instance);
        var result = new DatabaseMigrationBackupHookResult
        {
            Outcome = "failed",
            Succeeded = false,
            StartedAt = new DateTimeOffset(2026, 7, 9, 10, 15, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 9, 10, 15, 2, TimeSpan.Zero),
            DurationMilliseconds = 2_100,
            ExitCode = 2,
            Stderr = "pg_dump: permission denied",
            PendingContractScripts = ["002_drop_legacy_annotated.sql"],
            MigrationRunId = "schema-migration-test",
            CorrelationId = "schema-migration-test"
        };

        await recorder.RecordAsync(result);

        state.Latest.Should().BeSameAs(result);
        var evt = audit.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("migration.backup_hook");
        evt.ActorType.Should().Be(AuditActorType.System);
        evt.ResourceType.Should().Be("database_migration");
        evt.ResourceId.Should().Be("schema-migration-test");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
        evt.CorrelationId.Should().Be("schema-migration-test");

        using var details = JsonDocument.Parse(evt.Details);
        var root = details.RootElement;
        root.GetProperty("outcome").GetString().Should().Be("failed");
        root.GetProperty("durationMilliseconds").GetInt64().Should().Be(2_100);
        root.GetProperty("stderr").GetString().Should().Be("pg_dump: permission denied");
        root.GetProperty("pendingContractScripts")[0].GetString().Should().Be("002_drop_legacy_annotated.sql");
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
