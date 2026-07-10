// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class AuditingMigrationBackupHookRecorder(
    MigrationBackupHookState state,
    IServiceScopeFactory scopeFactory,
    ILogger<AuditingMigrationBackupHookRecorder> logger) : IDatabaseMigrationBackupHookRecorder
{
    private const string Actor = "database-migration-runner";
    private const string Action = "migration.backup_hook";
    private const string ResourceType = "database_migration";

    public async Task RecordAsync(
        DatabaseMigrationBackupHookResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        state.Record(result);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var auditLog = scope.ServiceProvider.GetService<IAuditLog>();
            if (auditLog is null)
            {
                return;
            }

            await auditLog.RecordAsync(
                new AuditEvent
                {
                    Timestamp = result.CompletedAt,
                    EventType = AuditEventType.AdminAction,
                    Actor = Actor,
                    ActorType = AuditActorType.System,
                    ResourceType = ResourceType,
                    ResourceId = result.MigrationRunId,
                    Action = Action,
                    Outcome = result.Succeeded ? AuditOutcome.Success : AuditOutcome.Failure,
                    CorrelationId = result.CorrelationId ?? result.MigrationRunId ?? ResourceType,
                    Details = BuildDetails(result),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AuditWriteFailed(logger, ex);
        }
    }

    private static string BuildDetails(DatabaseMigrationBackupHookResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", result.Outcome);
            writer.WriteBoolean("succeeded", result.Succeeded);
            writer.WriteString("startedAt", result.StartedAt);
            writer.WriteString("completedAt", result.CompletedAt);
            writer.WriteNumber("durationMilliseconds", result.DurationMilliseconds);
            if (result.ExitCode is { } exitCode)
            {
                writer.WriteNumber("exitCode", exitCode);
            }

            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                writer.WriteString("stderr", result.Stderr);
            }

            if (!string.IsNullOrWhiteSpace(result.MigrationRunId))
            {
                writer.WriteString("migrationRunId", result.MigrationRunId);
            }

            if (!string.IsNullOrWhiteSpace(result.CorrelationId))
            {
                writer.WriteString("correlationId", result.CorrelationId);
            }

            writer.WriteStartArray("pendingContractScripts");
            foreach (var script in result.PendingContractScripts)
            {
                writer.WriteStringValue(script);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7410,
            Level = LogLevel.Warning,
            Message = "Failed to record migration backup hook audit event")]
        public static partial void AuditWriteFailed(ILogger logger, Exception exception);
    }
}
